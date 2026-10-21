using System.Text.Json;
using AgenticHotelBooking.Application;

namespace AgenticHotelBooking.Api;

public static partial class DevelopmentApprovalEndpoints
{
    public static void MapDevelopmentApproval(this WebApplication app)
    {
        app.MapGet("/api/delivery/approvals/{pullRequestNumber:int}", async (
            int pullRequestNumber, int workItemId, HttpRequest request, KnowledgeSignInService signIn,
            IDevelopmentApprovalService service, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            if (await AdministratorAsync(request, signIn) is null) return Results.Unauthorized();
            try { return Results.Ok(await service.GetStateAsync(workItemId, pullRequestNumber, cancellationToken)); }
            catch (DevelopmentApprovalException exception) { return Results.Problem(statusCode: exception.StatusCode, title: exception.Message); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or TaskCanceledException)
            {
                VerificationFailed(loggers.CreateLogger("DevelopmentApproval"), exception);
                return Results.Problem(statusCode: 502, title: "Approval evidence could not be verified. Publishing remains paused.");
            }
        }).WithSummary("Read exact-revision demo approval readiness. Requires an approved administrator email session.");

        app.MapPost("/api/delivery/approvals", async (
            ApproveDevelopmentRequest approval, HttpRequest request, KnowledgeSignInService signIn,
            IDevelopmentApprovalService service, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            var administrator = await AdministratorAsync(request, signIn);
            if (administrator is null) return Results.Unauthorized();
            try { return Results.Ok(await service.ApproveAsync(approval, administrator.Email, cancellationToken)); }
            catch (DevelopmentApprovalException exception) { return Results.Problem(statusCode: exception.StatusCode, title: exception.Message); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or TaskCanceledException)
            {
                VerificationFailed(loggers.CreateLogger("DevelopmentApproval"), exception);
                return Results.Problem(statusCode: 502, title: "Approval could not be confirmed. Refresh to check its status before retrying.");
            }
        }).WithSummary("Record human demo approval and conditionally merge the exact fully verified revision. Never approves production.");
    }

    private static Task<AdministratorProfile?> AdministratorAsync(HttpRequest request, KnowledgeSignInService signIn)
    {
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? signIn.GetProfileAsync(authorization[7..]) : Task.FromResult<AdministratorProfile?>(null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Development approval verification failed; no success is assumed.")]
    private static partial void VerificationFailed(ILogger logger, Exception exception);
}
