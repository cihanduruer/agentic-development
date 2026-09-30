using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AgenticHotelBooking.Infrastructure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ModelContextProtocol.Server;

namespace AgenticHotelBooking.Tools.KnowledgeMcp;

public sealed class KnowledgeMcpTools(KnowledgeSearchService searchService)
{
    [McpServerTool(Name = "search_knowledge", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search canonical repository knowledge at one exact Git commit revision. Returns cited passages or an explicit no-evidence result.")]
    public async Task<string> SearchKnowledgeAsync(
        [Description("A bounded natural-language question, up to 256 characters.")]
        string query,
        [Required]
        [Description("The exact full 40-character Git commit SHA to search. No other revision is used.")]
        string revision,
        CancellationToken cancellationToken)
    {
        var result = await searchService.SearchAsync(query, revision, cancellationToken);
        return JsonSerializer.Serialize(result);
    }
}

public sealed record KnowledgeSearchResult(
    string? Revision,
    bool HasEvidence,
    string Status,
    string Message,
    IReadOnlyList<KnowledgePassage> Passages);

public sealed record KnowledgePassage(
    string Path,
    string SourceLink,
    string Title,
    string Passage,
    bool PassageTruncated,
    string Owner,
    string LastReviewed,
    string ContentHash);

public interface IKnowledgeSearchRepository
{
    Task<IReadOnlyList<KnowledgeSearchDocument>> SearchAsync(
        string query,
        string revision,
        int maximumResults,
        CancellationToken cancellationToken);
}

public sealed class AzureKnowledgeSearchRepository(SearchClient searchClient) : IKnowledgeSearchRepository
{
    public async Task<IReadOnlyList<KnowledgeSearchDocument>> SearchAsync(
        string query,
        string revision,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var response = await searchClient.SearchAsync<KnowledgeSearchDocument>(
            query,
            CreateSearchOptions(revision, maximumResults),
            cancellationToken);
        var documents = new List<KnowledgeSearchDocument>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            documents.Add(result.Document);
        }

        return documents;
    }

    public static SearchOptions CreateSearchOptions(string revision, int maximumResults)
    {
        var escapedRevision = revision.Replace("'", "''", StringComparison.Ordinal);
        return new SearchOptions
        {
            Filter = $"{nameof(KnowledgeSearchDocument.Revision)} eq '{escapedRevision}' and " +
                "startswith(Path, 'docs/knowledge/')",
            SearchMode = SearchMode.Any,
            QueryType = SearchQueryType.Simple,
            SearchFields = { nameof(KnowledgeSearchDocument.Title), nameof(KnowledgeSearchDocument.Content) },
            Size = maximumResults,
            Select =
            {
                nameof(KnowledgeSearchDocument.Id),
                nameof(KnowledgeSearchDocument.Path),
                nameof(KnowledgeSearchDocument.Revision),
                nameof(KnowledgeSearchDocument.Owner),
                nameof(KnowledgeSearchDocument.LastReviewed),
                nameof(KnowledgeSearchDocument.Title),
                nameof(KnowledgeSearchDocument.Content),
                nameof(KnowledgeSearchDocument.ContentHash)
            }
        };
    }
}

public sealed class KnowledgeSearchService(IKnowledgeSearchRepository repository)
{
    public const string IndexName = "knowledge";
    public const int MaximumQueryLength = 256;
    public const int MaximumResults = 5;
    public const int MaximumPassageLength = 3000;

    private static readonly Uri RepositoryRoot = new("https://github.com/cihanduruer/agentic-development/blob/");

    public async Task<KnowledgeSearchResult> SearchAsync(
        string? query,
        string? revision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return NoEvidence(null, "revision_required", "A full Git commit SHA revision is required.");
        }

        if (revision.Length != 40 || !revision.All(Uri.IsHexDigit))
        {
            return NoEvidence(revision, "invalid_revision", "Revision must be a full 40-character Git commit SHA.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return NoEvidence(revision, "query_required", "A non-empty knowledge query is required.");
        }

        var boundedQuery = query.Trim();
        if (boundedQuery.Length > MaximumQueryLength)
        {
            return NoEvidence(
                revision,
                "query_too_long",
                $"Knowledge queries are limited to {MaximumQueryLength} characters.");
        }

        var normalizedRevision = revision.ToLowerInvariant();
        var documents = await repository.SearchAsync(
            boundedQuery,
            normalizedRevision,
            MaximumResults,
            cancellationToken);
        var passages = documents
            .Where(document =>
                string.Equals(document.Revision, normalizedRevision, StringComparison.Ordinal) &&
                IsCanonicalKnowledgePath(document.Path))
            .Take(MaximumResults)
            .Select(ToPassage)
            .ToArray();

        return passages.Length == 0
            ? NoEvidence(
                normalizedRevision,
                "no_evidence",
                $"No matching knowledge evidence exists at revision {normalizedRevision}.")
            : new KnowledgeSearchResult(
                normalizedRevision,
                true,
                "evidence_found",
                $"Found {passages.Length} knowledge passage(s) at revision {normalizedRevision}.",
                passages);
    }

    private static KnowledgeSearchResult NoEvidence(string? revision, string status, string message) =>
        new(revision, false, status, message, []);

    private static KnowledgePassage ToPassage(KnowledgeSearchDocument document)
    {
        var truncated = document.Content.Length > MaximumPassageLength;
        var passage = truncated ? document.Content[..MaximumPassageLength] : document.Content;
        var escapedPath = string.Join(
            '/',
            document.Path.Split('/').Select(Uri.EscapeDataString));
        var sourceLink = new Uri(RepositoryRoot, $"{document.Revision}/{escapedPath}").AbsoluteUri;

        return new KnowledgePassage(
            document.Path,
            sourceLink,
            document.Title,
            passage,
            truncated,
            document.Owner,
            document.LastReviewed,
            document.ContentHash);
    }

    private static bool IsCanonicalKnowledgePath(string path)
    {
        if (!path.StartsWith("docs/knowledge/", StringComparison.Ordinal) ||
            path.Contains('\\'))
        {
            return false;
        }

        return path.Split('/').All(segment => segment is not ("." or ".."));
    }
}

public static class KnowledgeMcpToken
{
    public const int MinimumLength = 32;

    public static bool IsAuthorized(string? authorizationHeader, string accessToken)
    {
        if (string.IsNullOrEmpty(authorizationHeader) ||
            string.IsNullOrEmpty(accessToken) ||
            accessToken.Length < MinimumLength)
        {
            return false;
        }

        var expected = System.Text.Encoding.UTF8.GetBytes("Bearer " + accessToken);
        var actual = System.Text.Encoding.UTF8.GetBytes(authorizationHeader);
        return expected.Length == actual.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
