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
            Filter = $"{nameof(KnowledgeSearchHit.Revision)} eq '{escapedRevision}'",
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

        if (boundedQuery.Contains('*'))
        {
            return NoEvidence(
                revision,
                "invalid_query",
                "Knowledge queries cannot contain wildcard operators.");
        }

        var queryTerms = GetQueryTerms(boundedQuery);
        if (queryTerms.Length == 0)
        {
            return NoEvidence(
                revision,
                "invalid_query",
                "Knowledge queries must contain at least one searchable term.");
        }

        var normalizedRevision = revision.ToLowerInvariant();
        var documents = await repository.SearchAsync(
            string.Join(' ', queryTerms),
            normalizedRevision,
            MaximumResults,
            cancellationToken);
        var passages = documents
            .Where(document =>
                string.Equals(document.Revision, normalizedRevision, StringComparison.Ordinal) &&
                IsCanonicalKnowledgePath(document.Path))
            .Take(MaximumResults)
            .Select(document => ToPassage(document, queryTerms))
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

    private static string[] GetQueryTerms(string query)
    {
        var terms = new List<string>();
        var termStart = -1;
        for (var index = 0; index < query.Length; index++)
        {
            if (char.IsLetterOrDigit(query[index]))
            {
                termStart = termStart < 0 ? index : termStart;
            }
            else if (termStart >= 0)
            {
                terms.Add(query[termStart..index]);
                termStart = -1;
            }
        }

        if (termStart >= 0)
        {
            terms.Add(query[termStart..]);
        }

        return [.. terms];
    }

    private static KnowledgePassage ToPassage(KnowledgeSearchHit document, IReadOnlyList<string> queryTerms)
    {
        var truncated = document.Content.Length > MaximumPassageLength;
        var passageStart = truncated ? FindExcerptStart(document.Content, queryTerms) : 0;
        var passageLength = Math.Min(MaximumPassageLength, document.Content.Length - passageStart);
        var passage = document.Content.Substring(passageStart, passageLength);
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

    private static int FindExcerptStart(string content, IReadOnlyList<string> queryTerms)
    {
        var matchStart = -1;
        foreach (var term in queryTerms)
        {
            var occurrence = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (occurrence >= 0)
            {
                var matchEnd = occurrence + term.Length;
                var startsTerm = occurrence == 0 || !char.IsLetterOrDigit(content[occurrence - 1]);
                var endsTerm = matchEnd == content.Length || !char.IsLetterOrDigit(content[matchEnd]);
                if (startsTerm && endsTerm)
                {
                    matchStart = matchStart < 0 ? occurrence : Math.Min(matchStart, occurrence);
                    break;
                }

                occurrence = content.IndexOf(term, occurrence + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (matchStart < 0)
        {
            return 0;
        }

        var start = Math.Max(0, matchStart - MaximumPassageLength / 2);
        return start + MaximumPassageLength > content.Length
            ? content.Length - MaximumPassageLength
            : start;
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
            segments[^1].EndsWith(".md", StringComparison.Ordinal) &&
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
        if (!KnowledgeMcpToken.IsAuthorized(context.Request.Headers.Authorization, accessToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
