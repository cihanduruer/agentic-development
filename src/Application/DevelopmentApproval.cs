namespace AgenticHotelBooking.Application;

/// <summary>Describes demo approval readiness for an exact revision and acceptance-metadata digest.</summary>
public sealed record DevelopmentApprovalState(
    int PullRequestNumber, string HeadSha, bool CanApprove, string Status, string Message,
    string? ApprovalUrl = null, string? ApprovedBy = null, DateTimeOffset? ApprovedAt = null,
    string? MetadataDigest = null);

/// <summary>Binds administrator approval to the revision and acceptance evidence previously reviewed.</summary>
public sealed record ApproveDevelopmentRequest(
    int WorkItemId, int PullRequestNumber, string ExpectedHeadSha, string ExpectedMetadataDigest = "");

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
