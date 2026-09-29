using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticHotelBooking.Application;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace AgenticHotelBooking.Infrastructure;

public sealed record RoutingEvaluationOptions(
    bool Enabled,
    string? ContentSafetyEndpoint,
    string? SearchEndpoint,
    string SearchIndex);

public sealed record RoutingEvaluationResult(bool Passed, string ReasonCode, string Reason)
{
    public static RoutingEvaluationResult Allow() => new(true, "passed", "Safety and grounding checks passed.");

    public static RoutingEvaluationResult Reject(string code, string reason) => new(false, code, reason);
}

public interface IRoutingEvaluationGate
{
    Task<RoutingEvaluationResult> EvaluateAsync(
        RoutingRequest request,
        CancellationToken cancellationToken);
}

public interface IPromptShield
{
    Task<bool> IsAttackDetectedAsync(
        RoutingRequest request,
        CancellationToken cancellationToken);
}

public interface IKnowledgeGroundingEvaluator
{
    Task<bool> HasGroundingAsync(
        RoutingRequest request,
        CancellationToken cancellationToken);
}

public sealed class MicrosoftRoutingEvaluationGate(
    RoutingEvaluationOptions options,
    IPromptShield promptShield,
    IKnowledgeGroundingEvaluator groundingEvaluator) : IRoutingEvaluationGate
{
    public async Task<RoutingEvaluationResult> EvaluateAsync(
        RoutingRequest request,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return RoutingEvaluationResult.Allow();
        }

        if (string.IsNullOrWhiteSpace(options.ContentSafetyEndpoint) ||
            string.IsNullOrWhiteSpace(options.SearchEndpoint))
        {
            return RoutingEvaluationResult.Reject(
                "evaluation_not_configured",
                "Required Microsoft safety and grounding services are not configured.");
        }

        if (await promptShield.IsAttackDetectedAsync(request, cancellationToken))
        {
            return RoutingEvaluationResult.Reject(
                "prompt_attack",
                "Azure AI Content Safety Prompt Shields detected a prompt attack.");
        }

        if (!await groundingEvaluator.HasGroundingAsync(request, cancellationToken))
        {
            return RoutingEvaluationResult.Reject(
                "knowledge_revision_not_grounded",
                "Azure AI Search found no grounding evidence for the requested knowledge revision.");
        }

        return RoutingEvaluationResult.Allow();
    }
}

public sealed class AzurePromptShield(
    RoutingEvaluationOptions options,
    TokenCredential credential,
    HttpClient httpClient) : IPromptShield
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    public async Task<bool> IsAttackDetectedAsync(
        RoutingRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            new Uri(options.ContentSafetyEndpoint!, UriKind.Absolute),
            "contentsafety/text:shieldPrompt?api-version=2024-09-01");
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                userPrompt = $"{request.TaskCategory}\n{request.RequiredCapability}",
                documents = request.AvailableWorkers.Select(worker => $"{worker.Key}: {worker.Value}").ToArray()
            })
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var result = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        if (AttackDetected(result.RootElement, "userPromptAnalysis"))
        {
            return true;
        }

        return result.RootElement.TryGetProperty("documentsAnalysis", out var documents) &&
               documents.EnumerateArray().Any(item =>
                   item.TryGetProperty("attackDetected", out var detected) && detected.GetBoolean());
    }

    private static bool AttackDetected(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var analysis) &&
        analysis.TryGetProperty("attackDetected", out var detected) &&
        detected.GetBoolean();
}

public sealed class AzureSearchGroundingEvaluator : IKnowledgeGroundingEvaluator
{
    private readonly RoutingEvaluationOptions options;
    private readonly TokenCredential credential;

    public AzureSearchGroundingEvaluator(RoutingEvaluationOptions options, TokenCredential credential)
    {
        this.options = options;
        this.credential = credential;
    }

    public async Task<bool> HasGroundingAsync(
        RoutingRequest request,
        CancellationToken cancellationToken)
    {
        var searchClient = new SearchClient(
            new Uri(options.SearchEndpoint!, UriKind.Absolute),
            options.SearchIndex,
            credential);
        var escapedRevision = request.KnowledgeRevision.Replace("'", "''", StringComparison.Ordinal);
        var response = await searchClient.SearchAsync<KnowledgeSearchDocument>(
            $"{request.TaskCategory} {request.RequiredCapability}",
            new SearchOptions
            {
                Filter = $"{nameof(KnowledgeSearchDocument.Revision)} eq '{escapedRevision}'",
                Size = 1,
                Select = { nameof(KnowledgeSearchDocument.Id) }
            },
            cancellationToken);

        await foreach (var _ in response.Value.GetResultsAsync())
        {
            return true;
        }

        return false;
    }
}
