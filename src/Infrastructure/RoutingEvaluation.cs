using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                userPrompt = $"{request.TaskCategory}\n{request.RequiredCapability}\n{request.Risk}",
                documents = request.AvailableWorkers.Select(worker => $"{worker.Key}: {worker.Value}").ToArray()
            })
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var result = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        if (ReadAttackDetected(result.RootElement, "userPromptAnalysis"))
        {
            return true;
        }

        if (!result.RootElement.TryGetProperty("documentsAnalysis", out var documents) ||
            documents.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Prompt Shields response omitted document analysis.");
        }

        var analyses = documents.EnumerateArray().ToArray();
        if (analyses.Length != request.AvailableWorkers.Count)
        {
            throw new InvalidDataException("Prompt Shields returned an unexpected document count.");
        }

        return analyses.Any(ReadAttackDetected);
    }

    private static bool ReadAttackDetected(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var analysis))
        {
            throw new InvalidDataException($"Prompt Shields response omitted {propertyName}.");
        }

        return ReadAttackDetected(analysis);
    }

    private static bool ReadAttackDetected(JsonElement analysis)
    {
        if (!analysis.TryGetProperty("attackDetected", out var detected) ||
            detected.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException("Prompt Shields response omitted attackDetected.");
        }

        return detected.GetBoolean();
    }
}

public sealed class AzureSearchGroundingEvaluator : IKnowledgeGroundingEvaluator
{
    private static readonly Regex SearchTerms = new(
        "[A-Za-z0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
        var query = BuildGroundingQuery(request.TaskCategory, request.RequiredCapability);
        var response = await searchClient.SearchAsync<KnowledgeSearchDocument>(
            query,
            new SearchOptions
            {
                Filter = $"{nameof(KnowledgeSearchDocument.Revision)} eq '{escapedRevision}'",
                SearchMode = SearchMode.All,
                QueryType = SearchQueryType.Simple,
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

    public static string BuildGroundingQuery(string taskCategory, string requiredCapability)
    {
        var terms = SearchTerms.Matches($"{taskCategory} {requiredCapability}")
            .Select(match => match.Value)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length < 2)
        {
            throw new InvalidDataException("Grounding queries require at least two literal search terms.");
        }

        return string.Join(' ', terms);
    }
}
