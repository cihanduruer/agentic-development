using System.Collections.Concurrent;
using AgenticHotelBooking.Application;

namespace AgenticHotelBooking.Infrastructure;

public sealed class InMemoryAgentEventStore : IAgentEventStore
{
    private const int Capacity = 2_000;
    private readonly ConcurrentQueue<AgentEvent> events = new();

    public IReadOnlyList<AgentEvent> GetRecent(int limit = 100) =>
        events.Reverse().Take(Math.Clamp(limit, 1, 500)).ToArray();

    public AgentEvent Record(RecordAgentEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CorrelationId) ||
            string.IsNullOrWhiteSpace(request.Agent) ||
            string.IsNullOrWhiteSpace(request.Summary))
        {
            throw new ArgumentException("Correlation ID, agent, and summary are required.");
        }

        if (request.Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Confidence must be between 0 and 1.");
        }

        var recorded = new AgentEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.Kind,
            request.CorrelationId.Trim(),
            request.WorkItemId.Trim(),
            request.Agent.Trim(),
            request.Summary.Trim(),
            request.Decision.Trim(),
            request.Outcome.Trim(),
            request.Confidence,
            Math.Max(0, request.DurationMilliseconds),
            request.KnowledgeRevision.Trim());

        events.Enqueue(recorded);
        while (events.Count > Capacity)
        {
            events.TryDequeue(out _);
        }

        return recorded;
    }
}
