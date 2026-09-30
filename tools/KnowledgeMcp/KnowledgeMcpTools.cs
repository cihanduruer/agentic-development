using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
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
        string? revision = null,
        CancellationToken cancellationToken = default)
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

public sealed class KnowledgeSearchHit
{
    [SimpleField(IsKey = true)]
    public required string Id { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Path { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Revision { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Owner { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string LastReviewed { get; init; }
    [SearchableField]
    public required string Title { get; init; }
    [SearchableField]
    public required string Content { get; init; }
    [SimpleField]
    public required string ContentHash { get; init; }
}

public interface IKnowledgeSearchRepository
{
    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(
        string query,
        string revision,
        int maximumResults,
        CancellationToken cancellationToken);
}

public sealed class AzureKnowledgeSearchRepository(SearchClient searchClient) : IKnowledgeSearchRepository
{
    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(
        string query,
        string revision,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var response = await searchClient.SearchAsync<KnowledgeSearchHit>(
            query,
            CreateSearchOptions(revision, maximumResults),
            cancellationToken);
        var documents = new List<KnowledgeSearchHit>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            documents.Add(result.Document);
        }

        return documents;
    }

    public static SearchOptions CreateSearchOptions(string revision, int maximumResults)
    {
        if (maximumResults is < 1 or > KnowledgeSearchService.MaximumResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResults),
                $"Search result count must be between 1 and {KnowledgeSearchService.MaximumResults}.");
        }

        var escapedRevision = revision.Replace("'", "''", StringComparison.Ordinal);
        return new SearchOptions
        {
            Filter = $"{nameof(KnowledgeSearchHit.Revision)} eq '{escapedRevision}' and " +
                "startswith(Path, 'docs/knowledge/')",
            SearchMode = SearchMode.Any,
            QueryType = SearchQueryType.Simple,
            SearchFields = { nameof(KnowledgeSearchHit.Title), nameof(KnowledgeSearchHit.Content) },
            Size = maximumResults,
            Select =
            {
                nameof(KnowledgeSearchHit.Id),
                nameof(KnowledgeSearchHit.Path),
                nameof(KnowledgeSearchHit.Revision),
                nameof(KnowledgeSearchHit.Owner),
                nameof(KnowledgeSearchHit.LastReviewed),
                nameof(KnowledgeSearchHit.Title),
                nameof(KnowledgeSearchHit.Content),
                nameof(KnowledgeSearchHit.ContentHash)
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
            return NoEvidence(null, "invalid_revision", "Revision must be a full 40-character Git commit SHA.");
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

    private static KnowledgePassage ToPassage(KnowledgeSearchHit document)
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

        var segments = path.Split('/');
        return segments.Length > 2 &&
            segments[^1].EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
            segments.All(segment =>
                segment.Length > 0 &&
                segment is not ("." or "..") &&
                !segment.Any(char.IsControl));
    }
}

public static class KnowledgeMcpToken
{
    public const int MinimumLength = 32;
    public const int MaximumLength = 128;

    public static bool IsValidAccessToken(string? accessToken) =>
        accessToken is { Length: >= MinimumLength and <= MaximumLength } &&
        accessToken.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    public static bool IsAuthorized(string? authorizationHeader, string accessToken)
    {
        if (string.IsNullOrEmpty(authorizationHeader) ||
            !IsValidAccessToken(accessToken) ||
            authorizationHeader.Length != accessToken.Length + "Bearer ".Length)
        {
            return false;
        }

        var expected = System.Text.Encoding.UTF8.GetBytes("Bearer " + accessToken);
        var actual = System.Text.Encoding.UTF8.GetBytes(authorizationHeader);
        return expected.Length == actual.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public sealed class KnowledgeMcpAuthorizationMiddleware(RequestDelegate next, string accessToken)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp") &&
            !KnowledgeMcpToken.IsAuthorized(context.Request.Headers.Authorization, accessToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
