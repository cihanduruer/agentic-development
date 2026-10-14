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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDeliveryStatusService>(services => new DeliveryStatusService(
    new HttpClient { BaseAddress = new Uri("https://api.github.com/") },
    new HttpClient { BaseAddress = new Uri("https://dev.azure.com/ai-enabled-ado-org/") },
    services.GetRequiredService<IConfiguration>(),
    services.GetRequiredService<Azure.Core.TokenCredential>(),
    services.GetRequiredService<TimeProvider>()));
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
var workItemChatOptions = new WorkItemChatOptions(
    builder.Configuration["WorkItemChat:Endpoint"],
    builder.Configuration["WorkItemChat:ChatDeployment"] ?? "gpt-6.1-sol",
    builder.Configuration["WorkItemChat:EmbeddingDeployment"] ?? "text-embedding-3-large",
    builder.Configuration["WorkItemChat:SearchEndpoint"],
    builder.Configuration["WorkItemChat:SearchIndex"] ?? "hotel-knowledge-center",
    builder.Configuration["WorkItemChat:Organization"] ?? "ai-enabled-ado-org",
    builder.Configuration["WorkItemChat:Project"] ?? "sample-project",
    builder.Configuration.GetValue("WorkItemChat:CreateEnabled", false));
builder.Services.AddSingleton(workItemChatOptions);
builder.Services.AddSingleton<KnowledgeCenterSearch>();
builder.Services.AddSingleton(new KnowledgeUploadOptions(
    builder.Configuration.GetValue("WorkItemChat:UploadEnabled", false)));
builder.Services.AddSingleton<KnowledgeUploadService>();
builder.Services.AddSingleton<IWorkItemChatService, WorkItemChatService>();
builder.Services.AddSingleton<IWorkItemWriter>(services => new AzureBoardsWorkItemWriter(
    new HttpClient(),
    services.GetRequiredService<WorkItemChatOptions>(),
    services.GetRequiredService<Azure.Core.TokenCredential>()));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5166"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();
var boardsAuthMode = Program.GetBoardsAuthModeForLog(builder.Configuration["BoardsStatus:AuthMode"]);
app.Logger.BoardsAuthMode(boardsAuthMode);

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
        exception is ArgumentException or BadHttpRequestException ? StatusCodes.Status400BadRequest :
        exception is InvalidOperationException ? StatusCodes.Status409Conflict :
        StatusCodes.Status500InternalServerError;
    var title = exception is BadHttpRequestException
        ? "The request body is invalid."
        : status == 500
            ? "An unexpected error occurred."
            : exception?.Message;

    context.Response.StatusCode = status;
    await Results.Problem(
        statusCode: status,
        title: title)
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

app.MapMethods(
    "/api/reservations/{reservationId:guid}",
    ["PUT", "PATCH"],
    () => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Confirmed reservations cannot be changed."));

app.MapGet("/api/operations/events", (
    int? limit,
    IAgentEventStore store,
    CancellationToken cancellationToken) =>
    store.GetRecentAsync(limit ?? 100, cancellationToken));

app.MapGet("/api/knowledge/blocks", async (
    KnowledgeCenterSearch knowledge,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await knowledge.ListAsync(cancellationToken));
    }
    catch (InvalidOperationException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The knowledge center is not configured in this environment.");
    }
    catch (Exception exception) when (exception is Azure.RequestFailedException or HttpRequestException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The knowledge center could not be read. Try again.");
    }
});

app.MapPost("/api/knowledge/upload", async (
    IFormFile file,
    KnowledgeUploadService uploads,
    CancellationToken cancellationToken) =>
{
    if (!uploads.Enabled)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Uploading knowledge is disabled in this environment.");
    }

    if (file.Length is <= 0 or > KnowledgeUploadLimits.MaxFileBytes)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: $"Files must be between 1 byte and {KnowledgeUploadLimits.MaxFileBytes / (1024 * 1024)} MB.");
    }

    try
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var document = UploadedDocumentExtractor.Extract(file.FileName, buffer.ToArray());
        return Results.Ok(await uploads.IngestAsync(file.FileName, document, cancellationToken));
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
    }
    catch (InvalidOperationException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The knowledge center is not configured in this environment.");
    }
    catch (Exception exception) when (exception is Azure.RequestFailedException or HttpRequestException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The knowledge center could not store the upload. Try again.");
    }
})
    .DisableAntiforgery()
    .WithMetadata(new RequestSizeLimitAttribute(KnowledgeUploadLimits.MaxFileBytes + 512 * 1024));

app.MapPost("/api/knowledge/text", async (
    TextUploadRequest request,
    KnowledgeUploadService uploads,
    CancellationToken cancellationToken) =>
{
    if (!uploads.Enabled)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Uploading knowledge is disabled in this environment.");
    }

    try
    {
        var document = UploadedDocumentExtractor.ExtractText(request?.Title ?? string.Empty, request?.Text ?? string.Empty);
        return Results.Ok(await uploads.IngestAsync(request!.Title.Trim() + ".txt", document, cancellationToken));
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
    }
    catch (InvalidOperationException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The knowledge center is not configured in this environment.");
    }
    catch (Exception exception) when (exception is Azure.RequestFailedException or HttpRequestException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The knowledge center could not store the text. Try again.");
    }
});

app.MapPost("/api/knowledge/uploads/approve", async (
    string path,
    KnowledgeUploadService uploads,
    CancellationToken cancellationToken) =>
{
    if (!uploads.Enabled)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Uploading knowledge is disabled in this environment.");
    }

    if (!KnowledgeUploadService.IsUploadPath(path))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Only uploaded sources can be approved.");
    }

    try
    {
        var approved = await uploads.ApproveAsync(path, cancellationToken);
        return approved == 0
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "That source was not found.")
            : Results.Ok(new { approved });
    }
    catch (Exception exception) when (exception is Azure.RequestFailedException or HttpRequestException or InvalidOperationException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The knowledge center could not approve the source. Try again.");
    }
});

app.MapDelete("/api/knowledge/uploads", async (
    string path,
    KnowledgeUploadService uploads,
    CancellationToken cancellationToken) =>
{
    if (!uploads.Enabled)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Uploading knowledge is disabled in this environment.");
    }

    if (!KnowledgeUploadService.IsUploadPath(path))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Only uploaded sources can be removed.");
    }

    try
    {
        return Results.Ok(new { removed = await uploads.RemoveAsync(path, cancellationToken) });
    }
    catch (Exception exception) when (exception is Azure.RequestFailedException or HttpRequestException or InvalidOperationException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The knowledge center could not remove the source. Try again.");
    }
});

app.MapPost("/api/work-item-chat", async (
    WorkItemChatRequest request,
    IWorkItemChatService chat,
    CancellationToken cancellationToken) =>
{
    var problem = WorkItemChatValidator.ValidateRequest(request);
    if (problem is not null)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: problem);
    }

    try
    {
        return Results.Ok(await chat.ReplyAsync(request, cancellationToken));
    }
    catch (InvalidOperationException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The work item assistant is not configured in this environment.");
    }
    catch (Exception exception) when (exception is Azure.RequestFailedException or HttpRequestException or System.Text.Json.JsonException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The work item assistant could not complete the request. Try again.");
    }
});

app.MapPost("/api/work-items", async (
    WorkItemDraft draft,
    IWorkItemWriter writer,
    CancellationToken cancellationToken) =>
{
    var problems = WorkItemChatValidator.ValidateForCreation(draft);
    if (problems.Count > 0)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: string.Join(" ", problems));
    }

    try
    {
        var created = await writer.CreateUserStoryAsync(draft, cancellationToken);
        return Results.Created(created.Url, created);
    }
    catch (WorkItemCreationDisabledException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Creating Azure Boards work items is disabled in this environment.");
    }
    catch (Exception exception) when (exception is HttpRequestException or Azure.Identity.AuthenticationFailedException or Azure.RequestFailedException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Azure Boards could not create the work item.");
    }
});

app.MapGet("/api/delivery/status", async (
    string? workItemId,
    IDeliveryStatusService service,
    CancellationToken cancellationToken) =>
{
    int? selectedId = null;
    if (workItemId is not null)
    {
        if (!int.TryParse(workItemId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "workItemId must be a positive integer.");
        }

        selectedId = parsed;
    }

    try
    {
        return Results.Ok(await service.GetStatusAsync(selectedId, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "The work item is not present in recent GitHub deliveries.");
    }
});

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

    public static string GetBoardsAuthModeForLog(string? configuredMode) =>
        configuredMode is "ManagedIdentity" or "Pat" ? configuredMode : "Unconfigured";
}

internal static partial class DeliveryStatusLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Boards status authentication mode: {AuthMode}")]
    public static partial void BoardsAuthMode(this ILogger logger, string authMode);
}
