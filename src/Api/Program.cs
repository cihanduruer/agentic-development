using AgenticHotelBooking.Api;
using AgenticHotelBooking.Application;
using AgenticHotelBooking.Domain;
using AgenticHotelBooking.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddHotelBookingPersistence(builder.Configuration.GetConnectionString("HotelBooking"));
builder.Services.AddSingleton<IAgentEventStore, InMemoryAgentEventStore>();
builder.Services.AddSingleton(new JevRouterOptions(
    builder.Configuration.GetValue("Jev:Enabled", false),
    builder.Configuration.GetValue("Jev:Shadow", true),
    builder.Configuration.GetValue("Jev:MinimumConfidence", 0.8),
    builder.Configuration["Jev:Model"] ?? "jev-latest",
    builder.Configuration["TYPESAFE_API_KEY"]));
builder.Services.AddHttpClient<IAgentRouter, JevAgentRouter>(client =>
{
    client.BaseAddress = new Uri("https://api.typesafe.ai/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5166"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
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

app.MapGet("/api/operations/events", (int? limit, IAgentEventStore store) =>
    store.GetRecent(limit ?? 100));

app.MapPost("/api/operations/events", async (
    RecordAgentEventRequest request,
    IAgentEventStore store,
    IHubContext<OperationsHub> hub) =>
{
    var recorded = store.Record(request);
    await hub.Clients.All.SendAsync("AgentEventRecorded", recorded);
    return Results.Created($"/api/operations/events/{recorded.Id}", recorded);
});

app.MapPost("/api/orchestration/route", async (
    RoutingRequest request,
    IAgentRouter router,
    IAgentEventStore store,
    IHubContext<OperationsHub> hub,
    CancellationToken cancellationToken) =>
{
    var started = TimeProvider.System.GetTimestamp();
    var decision = await router.RouteAsync(request, cancellationToken);
    var recorded = store.Record(new RecordAgentEventRequest(
        AgentEventKind.RouteDecided,
        request.CorrelationId,
        request.WorkItemId,
        "jev",
        decision.Reason,
        decision.SuggestedWorker,
        decision.EffectiveWorker,
        decision.Confidence,
        (long)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
        request.KnowledgeRevision));
    await hub.Clients.All.SendAsync("AgentEventRecorded", recorded, cancellationToken);
    return Results.Ok(decision);
});

app.MapHub<OperationsHub>("/hubs/operations");

app.Run();

public partial class Program;
