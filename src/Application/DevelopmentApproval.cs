namespace AgenticHotelBooking.Application;

public sealed record DevelopmentApprovalState(
    int PullRequestNumber, string HeadSha, bool CanApprove, string Status, string Message,
    string? ApprovalUrl = null, string? ApprovedBy = null, DateTimeOffset? ApprovedAt = null);

public sealed record ApproveDevelopmentRequest(int WorkItemId, int PullRequestNumber, string ExpectedHeadSha);

public sealed record DevelopmentApprovalResponse(
    string Status, string Message, string? PullRequestUrl, string? ApprovalUrl, string? MergeSha);

public interface IDevelopmentApprovalService
{
    Task<DevelopmentApprovalState> GetStateAsync(int workItemId, int pullRequestNumber, CancellationToken cancellationToken);
    Task<DevelopmentApprovalResponse> ApproveAsync(ApproveDevelopmentRequest request, string administrator, CancellationToken cancellationToken);
}

public sealed class DevelopmentApprovalException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
