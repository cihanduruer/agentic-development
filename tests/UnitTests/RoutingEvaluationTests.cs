using AgenticHotelBooking.Application;
using AgenticHotelBooking.Infrastructure;
using Azure.Core;
using System.Net;
using System.Text.Json;

namespace AgenticHotelBooking.UnitTests;

public sealed class RoutingEvaluationTests
{
    [Fact]
    public async Task FailsClosedWhenEvaluationIsEnabledButNotConfigured()
    {
        var gate = new MicrosoftRoutingEvaluationGate(
            new RoutingEvaluationOptions(true, null, null, "knowledge"),
            new FakePromptShield(false),
            new FakeGroundingEvaluator(true));

        var result = await gate.EvaluateAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("evaluation_not_configured", result.ReasonCode);
    }

    [Fact]
    public async Task RejectsPromptAttackBeforeGrounding()
    {
        var grounding = new FakeGroundingEvaluator(true);
        var gate = CreateGate(new FakePromptShield(true), grounding);

        var result = await gate.EvaluateAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("prompt_attack", result.ReasonCode);
        Assert.Equal(0, grounding.CallCount);
    }

    [Fact]
    public async Task RejectsMissingRevisionGrounding()
    {
        var gate = CreateGate(new FakePromptShield(false), new FakeGroundingEvaluator(false));

        var result = await gate.EvaluateAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("knowledge_revision_not_grounded", result.ReasonCode);
    }

    [Fact]
    public async Task AllowsRequestOnlyAfterBothMicrosoftChecksPass()
    {
        var gate = CreateGate(new FakePromptShield(false), new FakeGroundingEvaluator(true));

        var result = await gate.EvaluateAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task PromptShieldInvokesMicrosoftApiAndDetectsDocumentAttack()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"userPromptAnalysis":{"attackDetected":false},"documentsAnalysis":[{"attackDetected":true},{"attackDetected":false}]}""")
            };
        });
        var shield = new AzurePromptShield(
            new RoutingEvaluationOptions(
                true,
                "https://example.cognitiveservices.azure.com/",
                "https://example.search.windows.net/",
                "knowledge"),
            new StubTokenCredential(),
            new HttpClient(handler));

        var detected = await shield.IsAttackDetectedAsync(CreateRequest(), CancellationToken.None);

        Assert.True(detected);
        Assert.NotNull(captured);
        Assert.Equal(
            "/contentsafety/text:shieldPrompt?api-version=2024-09-01",
            captured.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Contains(
            "reversible",
            capturedBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptShieldRejectsMalformedSuccessfulResponse()
    {
        var shield = new AzurePromptShield(
            new RoutingEvaluationOptions(
                true,
                "https://example.cognitiveservices.azure.com/",
                "https://example.search.windows.net/",
                "knowledge"),
            new StubTokenCredential(),
            new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"userPromptAnalysis":{}}""")
                })));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            shield.IsAttackDetectedAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public void GroundingQueryUsesOnlyLiteralTermsAndRequiresContext()
    {
        var query = AzureSearchGroundingEvaluator.BuildGroundingQuery(
            "quality-assurance",
            "browser-testing OR *");

        Assert.Equal("quality assurance browser testing OR", query);
        Assert.Throws<InvalidDataException>(() =>
            AzureSearchGroundingEvaluator.BuildGroundingQuery("*", "?"));
    }

    [Fact]
    public void GroundingSearchProjectionDeserializesIdOnlyDocuments()
    {
        var hit = JsonSerializer.Deserialize<KnowledgeSearchHit>(
            """{"Id":"document-id"}""");

        Assert.NotNull(hit);
        Assert.Equal("document-id", hit.Id);
    }

    [Fact]
    public void DevelopmentSmokeVocabularyIsGroundedByCanonicalKnowledge()
    {
        const string relativePath = "docs/knowledge/agentic-delivery.md";
        var repositoryRoot = FindRepositoryRoot();
        var chunks = KnowledgeDocumentChunker.Chunk(
            relativePath,
            File.ReadAllText(Path.Combine(repositoryRoot, relativePath)),
            "test-revision");
        var terms = AzureSearchGroundingEvaluator.BuildGroundingQuery(
                "quality-assurance",
                "api-testing")
            .Split(' ');

        Assert.Contains(
            chunks,
            chunk =>
            {
                var searchableText = $"{chunk.Title} {chunk.Content}";
                return terms.All(term =>
                    searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgenticHotelBooking.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private static MicrosoftRoutingEvaluationGate CreateGate(
        IPromptShield promptShield,
        IKnowledgeGroundingEvaluator grounding) =>
        new(
            new RoutingEvaluationOptions(
                true,
                "https://example.cognitiveservices.azure.com/",
                "https://example.search.windows.net/",
                "knowledge"),
            promptShield,
            grounding);

    private static RoutingRequest CreateRequest() =>
        new(
            "route-42",
            "AB#958",
            "quality-assurance",
            "browser-testing",
            "reversible",
            true,
            new Dictionary<string, string>
            {
                ["qa-agent"] = "Runs tests.",
                ["human_review"] = "Handles uncertain work."
            },
            "abc123");

    private sealed class FakePromptShield(bool attackDetected) : IPromptShield
    {
        public Task<bool> IsAttackDetectedAsync(
            RoutingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(attackDetected);
    }

    private sealed class FakeGroundingEvaluator(bool grounded) : IKnowledgeGroundingEvaluator
    {
        public int CallCount { get; private set; }

        public Task<bool> HasGroundingAsync(
            RoutingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(grounded);
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("token", DateTimeOffset.MaxValue));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
