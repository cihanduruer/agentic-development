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
