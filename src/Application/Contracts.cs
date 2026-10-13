using AgenticHotelBooking.Domain;

namespace AgenticHotelBooking.Application;

public interface IHotelBookingService
{
    Task<IReadOnlyList<Hotel>> GetHotelsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Room>> SearchAvailabilityAsync(
        Guid hotelId,
        DateOnly checkIn,
        DateOnly checkOut,
        int guests,
        CancellationToken cancellationToken);
    Task<Reservation> CreateReservationAsync(BookingRequest request, CancellationToken cancellationToken);
}

public enum AgentEventKind
{
    WorkReceived,
    RouteDecided,
    AgentStarted,
    ToolCalled,
    EvidenceChecked,
    HumanApprovalRequired,
    Completed,
    Failed
}

public sealed record AgentEvent(
    Guid Id,
    DateTimeOffset Timestamp,
    AgentEventKind Kind,
    string CorrelationId,
    string WorkItemId,
    string Agent,
    string Summary,
    string Decision,
    string Outcome,
    double? Confidence,
    long DurationMilliseconds,
    string KnowledgeRevision);

public sealed record RecordAgentEventRequest(
    AgentEventKind Kind,
    string CorrelationId,
    string WorkItemId,
    string Agent,
    string Summary,
    string Decision,
    string Outcome,
    double? Confidence,
    long DurationMilliseconds,
    string KnowledgeRevision);

public interface IAgentEventStore
{
    Task<IReadOnlyList<AgentEvent>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<AgentEvent> RecordAsync(
        RecordAgentEventRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RoutingRequest(
    string CorrelationId,
    string WorkItemId,
    string TaskCategory,
    string RequiredCapability,
    string Risk,
    bool EvidenceComplete,
    IReadOnlyDictionary<string, string> AvailableWorkers,
    string KnowledgeRevision);

public sealed record RoutingDecision(
    string SuggestedWorker,
    string EffectiveWorker,
    double Confidence,
    bool Shadow,
    string Model,
    string Reason);

public interface IAgentRouter
{
    Task<RoutingDecision> RouteAsync(RoutingRequest request, CancellationToken cancellationToken);
}

public sealed record DeliveryStatusSnapshot(
    DateTimeOffset RefreshedAtUtc,
    bool IsStale,
    string? UnreachableMessage,
    string BoardsConnection,
    IReadOnlyList<DeliverySummary> Deliveries)
{
    public DeliverySummary? SelectedDelivery { get; init; }
}

public sealed record DeliverySummary(
    int? WorkItemId,
    string GitHubIssueTitle,
    string GitHubIssueUrl,
    int GitHubIssueNumber,
    DateTimeOffset UpdatedAtUtc,
    string OverallStatus,
    string? PullRequestTitle,
    int? PullRequestNumber,
    string? PullRequestUrl,
    IReadOnlyList<DeliveryStageSummary> Stages,
    BoardsWorkItemSummary? BoardsWorkItem);

public sealed record DeliveryStageSummary(
    string Name,
    string Status,
    DateTimeOffset? UpdatedAtUtc,
    long? DurationMilliseconds,
    string? Url,
    string? HeadSha)
{
    public IReadOnlyList<DeliveryEvidenceLink> RelatedEvidence { get; init; } = [];
}

public sealed record DeliveryEvidenceLink(string Label, string Url);

public sealed record BoardsWorkItemSummary(
    int Id,
    string Title,
    string State,
    string Type,
    IReadOnlyList<string> Tags,
    DateTimeOffset? ChangedAtUtc,
    string Url,
    IReadOnlyList<DeliveryLinkSummary> Links);

public sealed record DeliveryLinkSummary(string Label, string Url);

public interface IDeliveryStatusService
{
    Task<DeliveryStatusSnapshot> GetStatusAsync(int? workItemId, CancellationToken cancellationToken);
}
