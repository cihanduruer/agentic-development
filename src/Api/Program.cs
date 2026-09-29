using AgenticHotelBooking.Api;
using AgenticHotelBooking.Application;
using AgenticHotelBooking.Domain;
using AgenticHotelBooking.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
var eventStoreOptions = new AgentEventStoreOptions(
    builder.Configuration.GetValue("OperationsEvents:RetentionDays", 30),
    builder.Configuration.GetValue("OperationsEvents:MaxRecords", 2_000),
    builder.Configuration.GetValue("OperationsEvents:MaxQueryLimit", 500));
builder.Services.AddHotelBookingPersistence(
    builder.Configuration.GetConnectionString("HotelBooking"),
    eventStoreOptions);
const string operationsWriterPolicy = "OperationsWriter";
if (!builder.Environment.IsDevelopment())
{
    var authority = builder.Configuration["OperationsAuth:Authority"];
    var audience = builder.Configuration["OperationsAuth:Audience"];
    var requiredRole = builder.Configuration["OperationsAuth:RequiredRole"];
    if (string.IsNullOrWhiteSpace(authority) ||
        string.IsNullOrWhiteSpace(audience) ||
        string.IsNullOrWhiteSpace(requiredRole))
    {
        throw new InvalidOperationException(
            "OperationsAuth Authority, Audience, and RequiredRole are required outside Development.");
    }

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                RoleClaimType = "roles"
            };
        });
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(operationsWriterPolicy, policy =>
            policy.RequireAuthenticatedUser().RequireRole(requiredRole));
}
builder.Services.AddHttpClient();
var routerOptions = new MicrosoftRouterOptions(
    builder.Configuration.GetValue("MicrosoftRouting:ModelEnabled", false),
    builder.Configuration.GetValue("MicrosoftRouting:MinimumConfidence", 0.8),
    builder.Configuration["MicrosoftRouting:Deployment"] ?? "gpt-4.1-mini",
    builder.Configuration["MicrosoftRouting:Endpoint"],
    builder.Configuration["MicrosoftRouting:PolicyVersion"] ?? "2026-09-29");
builder.Services.AddSingleton(routerOptions);
var evaluationOptions = new RoutingEvaluationOptions(
    builder.Configuration.GetValue("RoutingEvaluation:Enabled", false),
    builder.Configuration["RoutingEvaluation:ContentSafetyEndpoint"],
    builder.Configuration["RoutingEvaluation:SearchEndpoint"],
    builder.Configuration["RoutingEvaluation:SearchIndex"] ?? "knowledge");
builder.Services.AddSingleton(evaluationOptions);
builder.Services.AddSingleton<Azure.Core.TokenCredential, Azure.Identity.DefaultAzureCredential>();
builder.Services.AddSingleton<IPromptShield, AzurePromptShield>();
builder.Services.AddSingleton<IKnowledgeGroundingEvaluator, AzureSearchGroundingEvaluator>();
builder.Services.AddSingleton<IRoutingEvaluationGate, MicrosoftRoutingEvaluationGate>();
builder.Services.AddSingleton<IAmbiguousRouteResolver, MicrosoftAgentFrameworkRouteResolver>();
builder.Services.AddSingleton<IAgentRouter, MicrosoftAgentRouter>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5166"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

if (Program.ShouldApplyDatabaseMigrations(
        app.Environment.IsDevelopment(),
        builder.Configuration.GetValue("Database:ApplyMigrations", false)))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<HotelBookingDbContext>();
    if (database.Database.IsRelational())
    {
        await database.Database.MigrateAsync();
    }
    else
    {
        await database.Database.EnsureCreatedAsync();
    }
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception is KeyNotFoundException ? StatusCodes.Status404NotFound :
        exception is ArgumentException ? StatusCodes.Status400BadRequest :
        exception is InvalidOperationException ? StatusCodes.Status409Conflict :
        StatusCodes.Status500InternalServerError;

    context.Response.StatusCode = status;
    await Results.Problem(
        statusCode: status,
        title: status == 500 ? "An unexpected error occurred." : exception?.Message)
        .ExecuteAsync(context);
}));

app.UseCors();
if (!app.Environment.IsDevelopment())
{
    app.UseAuthentication();
    app.UseAuthorization();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/api/hotels", async (IHotelBookingService service, CancellationToken cancellationToken) =>
    await service.GetHotelsAsync(cancellationToken));

app.MapGet("/api/hotels/{hotelId:guid}/availability", (
    Guid hotelId,
    DateOnly checkIn,
    DateOnly checkOut,
    int guests,
    IHotelBookingService service,
    CancellationToken cancellationToken) =>
    service.SearchAvailabilityAsync(hotelId, checkIn, checkOut, guests, cancellationToken));

app.MapPost("/api/reservations", async (
    BookingRequest request,
    IHotelBookingService service,
    CancellationToken cancellationToken) =>
{
    var reservation = await service.CreateReservationAsync(request, cancellationToken);
    return Results.Created($"/api/reservations/{reservation.Id}", reservation);
});

app.MapGet("/api/operations/events", (
    int? limit,
    IAgentEventStore store,
    CancellationToken cancellationToken) =>
    store.GetRecentAsync(limit ?? 100, cancellationToken));

var ingestEvent = app.MapPost("/api/operations/events", async (
    RecordAgentEventRequest request,
    IAgentEventStore store,
    IHubContext<OperationsHub> hub,
    CancellationToken cancellationToken) =>
{
    var recorded = await store.RecordAsync(request, cancellationToken);
    await hub.Clients.All.SendAsync("AgentEventRecorded", recorded, cancellationToken);
    return Results.Created($"/api/operations/events/{recorded.Id}", recorded);
});

var routeWork = app.MapPost("/api/orchestration/route", async (
    RoutingRequest request,
    IAgentRouter router,
    IAgentEventStore store,
    IHubContext<OperationsHub> hub,
    CancellationToken cancellationToken) =>
{
    var started = TimeProvider.System.GetTimestamp();
    var decision = await router.RouteAsync(request, cancellationToken);
    var recorded = await store.RecordAsync(new RecordAgentEventRequest(
        AgentEventKind.RouteDecided,
        request.CorrelationId,
        request.WorkItemId,
        "microsoft-router",
        $"{decision.Model}: {decision.Reason}",
        decision.SuggestedWorker,
        decision.EffectiveWorker,
        decision.Confidence,
        (long)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
        request.KnowledgeRevision), cancellationToken);
    await hub.Clients.All.SendAsync("AgentEventRecorded", recorded, cancellationToken);
    return Results.Ok(decision);
});

if (!app.Environment.IsDevelopment())
{
    ingestEvent.RequireAuthorization(operationsWriterPolicy);
    routeWork.RequireAuthorization(operationsWriterPolicy);
}

app.MapHub<OperationsHub>("/hubs/operations");

app.Run();

public partial class Program
{
    public static bool ShouldApplyDatabaseMigrations(bool isDevelopment, bool configured) =>
        isDevelopment && configured;
}
