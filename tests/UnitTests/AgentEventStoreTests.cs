using AgenticHotelBooking.Application;
using AgenticHotelBooking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgenticHotelBooking.UnitTests;

public sealed class AgentEventStoreTests
{
    [Fact]
    public async Task PersistsEventsAcrossStoreInstances()
    {
        var databaseName = Guid.NewGuid().ToString();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));
        var request = CreateRequest("flow-persisted");

        await using (var writeContext = CreateContext(databaseName))
        {
            var writer = new EntityFrameworkAgentEventStore(
                writeContext,
                new AgentEventStoreOptions(),
                timeProvider);
            await writer.RecordAsync(request);
        }

        await using var readContext = CreateContext(databaseName);
        var reader = new EntityFrameworkAgentEventStore(
            readContext,
            new AgentEventStoreOptions(),
            timeProvider);
        var events = await reader.GetRecentAsync();

        var recorded = Assert.Single(events);
        Assert.Equal(request.CorrelationId, recorded.CorrelationId);
        Assert.Equal(timeProvider.GetUtcNow(), recorded.Timestamp);
    }

    [Fact]
    public async Task EnforcesRetentionAndQueryBounds()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        await using var context = CreateContext(Guid.NewGuid().ToString());
        var store = new EntityFrameworkAgentEventStore(
            context,
            new AgentEventStoreOptions(RetentionDays: 2, MaxRecords: 2, MaxQueryLimit: 1),
            timeProvider);

        await store.RecordAsync(CreateRequest("expired"));
        timeProvider.Advance(TimeSpan.FromDays(3));
        await store.RecordAsync(CreateRequest("retained-1"));
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await store.RecordAsync(CreateRequest("retained-2"));
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await store.RecordAsync(CreateRequest("newest"));

        var events = await store.GetRecentAsync(1000);

        var newest = Assert.Single(events);
        Assert.Equal("newest", newest.CorrelationId);
        Assert.Equal(2, await context.AgentEvents.CountAsync());
        Assert.DoesNotContain(
            await context.AgentEvents.ToArrayAsync(),
            item => item.CorrelationId is "expired" or "retained-1");
    }

    private static HotelBookingDbContext CreateContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<HotelBookingDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new HotelBookingDbContext(options);
    }

    private static RecordAgentEventRequest CreateRequest(string correlationId) =>
        new(
            AgentEventKind.RouteDecided,
            correlationId,
            "AB#958",
            "microsoft-router",
            "Selected a worker.",
            "qa-agent",
            "selected",
            0.9,
            10,
            "2dc64f5");

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
