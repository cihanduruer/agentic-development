using AgenticHotelBooking.Application;
using AgenticHotelBooking.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticHotelBooking.UnitTests;

public sealed class MicrosoftAgentRouterTests
{
    [Fact]
    public async Task RoutesToOnlyEligibleWorkerWithoutCallingModel()
    {
        var resolver = new FakeResolver();
        var router = CreateRouter(resolver);
        var request = CreateRequest(
            new Dictionary<string, string>
            {
                ["qa-agent"] = "Runs tests.",
                ["human_review"] = "Handles uncertain work."
            });

        var decision = await router.RouteAsync(request, CancellationToken.None);

        Assert.Equal("qa-agent", decision.EffectiveWorker);
        Assert.StartsWith("policy:", decision.Model);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData(false, "reversible")]
    [InlineData(true, "high")]
    [InlineData(true, "critical")]
    [InlineData(true, "irreversible")]
    public async Task RoutesUnsafeOrUngroundedWorkToHumanReview(bool evidenceComplete, string risk)
    {
        var resolver = new FakeResolver();
        var router = CreateRouter(resolver);
        var request = CreateRequest(
            new Dictionary<string, string>
            {
                ["developer"] = "Implements code.",
                ["qa-agent"] = "Runs tests.",
                ["human_review"] = "Handles uncertain work."
            },
            evidenceComplete,
            risk);

        var decision = await router.RouteAsync(request, CancellationToken.None);

        Assert.Equal("human_review", decision.EffectiveWorker);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task UsesTypedModelSuggestionForAmbiguousSafeRoute()
    {
        var resolver = new FakeResolver(new ModelRoutingSuggestion("qa-agent", 0.91, "test_work"));
        var router = CreateRouter(resolver);

        var decision = await router.RouteAsync(CreateAmbiguousRequest(), CancellationToken.None);

        Assert.Equal("qa-agent", decision.EffectiveWorker);
        Assert.Equal("gpt-4.1-mini", decision.Model);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task RoutesLowConfidenceModelSuggestionToHumanReview()
    {
        var resolver = new FakeResolver(new ModelRoutingSuggestion("qa-agent", 0.5, "uncertain"));
        var router = CreateRouter(resolver);

        var decision = await router.RouteAsync(CreateAmbiguousRequest(), CancellationToken.None);

        Assert.Equal("qa-agent", decision.SuggestedWorker);
        Assert.Equal("human_review", decision.EffectiveWorker);
    }

    [Fact]
    public async Task RoutesModelFailureToHumanReview()
    {
        var router = CreateRouter(new FakeResolver(exception: new HttpRequestException("Unavailable")));

        var decision = await router.RouteAsync(CreateAmbiguousRequest(), CancellationToken.None);

        Assert.Equal("human_review", decision.EffectiveWorker);
        Assert.Contains("model_error", decision.Reason);
    }

    [Fact]
    public async Task RoutesEvaluationFailureToHumanReviewWithoutCallingModel()
    {
        var resolver = new FakeResolver();
        var router = CreateRouter(resolver, new ThrowingEvaluationGate());

        var decision = await router.RouteAsync(CreateAmbiguousRequest(), CancellationToken.None);

        Assert.Equal("human_review", decision.EffectiveWorker);
        Assert.Contains("evaluation_error", decision.Reason);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task RoutesEvaluationTimeoutToHumanReviewWhenCallerDidNotCancel()
    {
        var resolver = new FakeResolver();
        var router = CreateRouter(resolver, new TimeoutEvaluationGate());

        var decision = await router.RouteAsync(CreateAmbiguousRequest(), CancellationToken.None);

        Assert.Equal("human_review", decision.EffectiveWorker);
        Assert.Contains("evaluation_error", decision.Reason);
        Assert.Equal(0, resolver.CallCount);
    }

    private static MicrosoftAgentRouter CreateRouter(
        IAmbiguousRouteResolver resolver,
        IRoutingEvaluationGate? evaluationGate = null) =>
        new(
            new MicrosoftRouterOptions(true, 0.8, "gpt-4.1-mini", "https://example.openai.azure.com", "test"),
            evaluationGate ?? new AllowEvaluationGate(),
            resolver,
            NullLogger<MicrosoftAgentRouter>.Instance);

    private static RoutingRequest CreateAmbiguousRequest() =>
        CreateRequest(new Dictionary<string, string>
        {
            ["developer"] = "Implements code.",
            ["qa-agent"] = "Runs tests.",
            ["human_review"] = "Handles uncertain work."
        });

    private static RoutingRequest CreateRequest(
        IReadOnlyDictionary<string, string> workers,
        bool evidenceComplete = true,
        string risk = "reversible") =>
        new(
            "route-42",
            "AB#958",
            "quality-assurance",
            "browser-testing",
            risk,
            evidenceComplete,
            workers,
            "abc123");

    private sealed class FakeResolver(
        ModelRoutingSuggestion? result = null,
        Exception? exception = null) : IAmbiguousRouteResolver
    {
        public int CallCount { get; private set; }

        public Task<ModelRoutingSuggestion> ResolveAsync(
            RoutingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return exception is null
                ? Task.FromResult(result ?? new ModelRoutingSuggestion("human_review", 1, "default"))
                : Task.FromException<ModelRoutingSuggestion>(exception);
        }

    }

    private sealed class AllowEvaluationGate : IRoutingEvaluationGate
    {
        public Task<RoutingEvaluationResult> EvaluateAsync(
            RoutingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(RoutingEvaluationResult.Allow());
    }

    private sealed class ThrowingEvaluationGate : IRoutingEvaluationGate
    {
        public Task<RoutingEvaluationResult> EvaluateAsync(
            RoutingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<RoutingEvaluationResult>(new HttpRequestException("Unavailable"));
    }

    private sealed class TimeoutEvaluationGate : IRoutingEvaluationGate
    {
        public Task<RoutingEvaluationResult> EvaluateAsync(
            RoutingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<RoutingEvaluationResult>(new TaskCanceledException("Service timeout"));
    }
}
