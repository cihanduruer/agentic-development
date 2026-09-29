using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticHotelBooking.Application;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.OpenAI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace AgenticHotelBooking.Infrastructure;

public sealed record MicrosoftRouterOptions(
    bool ModelEnabled,
    double MinimumConfidence,
    string Deployment,
    string? Endpoint,
    string PolicyVersion);

public sealed record ModelRoutingSuggestion(string Worker, double Confidence, string ReasonCode);

public interface IAmbiguousRouteResolver
{
    Task<ModelRoutingSuggestion> ResolveAsync(
        RoutingRequest request,
        CancellationToken cancellationToken);
}

public sealed partial class MicrosoftAgentRouter(
    MicrosoftRouterOptions options,
    IAmbiguousRouteResolver resolver,
    ILogger<MicrosoftAgentRouter> logger) : IAgentRouter
{
    private const string HumanReview = "human_review";

    public async Task<RoutingDecision> RouteAsync(
        RoutingRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);

        if (!request.EvidenceComplete)
        {
            return HumanDecision("evidence_incomplete", "Required evidence is incomplete.");
        }

        if (RequiresHumanApproval(request.Risk))
        {
            return HumanDecision("risk_policy", "Risk policy requires human approval.");
        }

        var workers = request.AvailableWorkers.Keys
            .Where(worker => !string.Equals(worker, HumanReview, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (workers.Length == 1)
        {
            return new RoutingDecision(
                workers[0],
                workers[0],
                1,
                false,
                $"policy:{options.PolicyVersion}",
                "Deterministic policy selected the only eligible worker.");
        }

        if (!options.ModelEnabled || string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return HumanDecision(
                "model_unavailable",
                "Multiple workers are eligible and model-assisted routing is not configured.");
        }

        try
        {
            var suggestion = await resolver.ResolveAsync(request, cancellationToken);
            var validWorker = request.AvailableWorkers.ContainsKey(suggestion.Worker)
                ? suggestion.Worker
                : HumanReview;
            var effectiveWorker = validWorker != HumanReview &&
                                  suggestion.Confidence >= options.MinimumConfidence
                ? validWorker
                : HumanReview;
            var reason = validWorker == HumanReview
                ? "The model suggestion was not in the live worker menu."
                : suggestion.Confidence < options.MinimumConfidence
                    ? "Model confidence was below the configured threshold."
                    : $"Microsoft Agent Framework selected a live worker ({suggestion.ReasonCode}).";

            return new RoutingDecision(
                validWorker,
                effectiveWorker,
                Math.Clamp(suggestion.Confidence, 0, 1),
                false,
                options.Deployment,
                reason);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogModelRoutingFailure(logger, exception, request.CorrelationId);
            return HumanDecision("model_error", "Model-assisted routing failed; human review is required.");
        }
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Model-assisted routing failed for {CorrelationId}")]
    private static partial void LogModelRoutingFailure(
        ILogger logger,
        Exception exception,
        string correlationId);

    private RoutingDecision HumanDecision(string reasonCode, string reason) =>
        new(
            HumanReview,
            HumanReview,
            1,
            false,
            $"policy:{options.PolicyVersion}",
            $"{reason} ({reasonCode})");

    private static bool RequiresHumanApproval(string risk) =>
        risk.Equals("high", StringComparison.OrdinalIgnoreCase) ||
        risk.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
        risk.Equals("irreversible", StringComparison.OrdinalIgnoreCase);

    private static void Validate(RoutingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CorrelationId) ||
            string.IsNullOrWhiteSpace(request.WorkItemId) ||
            string.IsNullOrWhiteSpace(request.TaskCategory) ||
            string.IsNullOrWhiteSpace(request.RequiredCapability) ||
            string.IsNullOrWhiteSpace(request.KnowledgeRevision))
        {
            throw new ArgumentException("Routing metadata and knowledge revision are required.");
        }

        if (request.AvailableWorkers.Count == 0 ||
            !request.AvailableWorkers.ContainsKey(HumanReview))
        {
            throw new ArgumentException("The live worker menu must include human_review.");
        }
    }
}

public sealed class MicrosoftAgentFrameworkRouteResolver : IAmbiguousRouteResolver
{
    private readonly Lazy<ChatClientAgent> agent;

    public MicrosoftAgentFrameworkRouteResolver(MicrosoftRouterOptions options)
    {
        agent = new Lazy<ChatClientAgent>(() => CreateAgent(options));
    }

    private static ChatClientAgent CreateAgent(MicrosoftRouterOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException(
                "MicrosoftRouting:Endpoint is required when model-assisted routing is enabled.");
        }

#pragma warning disable OPENAI001
        var client = new OpenAIClient(
            new BearerTokenPolicy(
                new DefaultAzureCredential(),
                "https://ai.azure.com/.default"),
            new OpenAIClientOptions
            {
                Endpoint = BuildOpenAiV1Endpoint(options.Endpoint)
            });
#pragma warning restore OPENAI001

        var createdAgent = client.GetChatClient(options.Deployment).AsAIAgent(
            name: "HotelWorkRouter",
            instructions:
                "Choose one worker from the supplied live menu. Use human_review when capability is unclear. " +
                "Never infer permissions or authorize irreversible actions.");

        return createdAgent;
    }

    private static Uri BuildOpenAiV1Endpoint(string endpoint)
    {
        var endpointUri = new Uri(endpoint, UriKind.Absolute);
        if (endpointUri.AbsolutePath.TrimEnd('/').EndsWith(
                "/openai/v1",
                StringComparison.OrdinalIgnoreCase))
        {
            return endpointUri;
        }

        return new UriBuilder(endpointUri)
        {
            Path = $"{endpointUri.AbsolutePath.TrimEnd('/')}/openai/v1/"
        }.Uri;
    }

    public async Task<ModelRoutingSuggestion> ResolveAsync(
        RoutingRequest request,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new
        {
            task_category = request.TaskCategory,
            required_capability = request.RequiredCapability,
            risk = request.Risk,
            evidence_complete = request.EvidenceComplete,
            available_workers = request.AvailableWorkers.Keys
        });
        var response = await agent.Value.RunAsync<AgentRouteOutput>(input, cancellationToken: cancellationToken);
        var result = response.Result;

        return new ModelRoutingSuggestion(
            result.Worker ?? "human_review",
            result.Confidence,
            result.ReasonCode ?? "unspecified");
    }

    [Description("A bounded routing suggestion from the live worker menu")]
    private sealed class AgentRouteOutput
    {
        [JsonPropertyName("worker")]
        [Description("The exact worker identifier selected from available_workers")]
        public string? Worker { get; set; }

        [JsonPropertyName("confidence")]
        [Description("Confidence from 0.0 to 1.0")]
        public double Confidence { get; set; }

        [JsonPropertyName("reason_code")]
        [Description("A short machine-readable reason code")]
        public string? ReasonCode { get; set; }
    }
}
