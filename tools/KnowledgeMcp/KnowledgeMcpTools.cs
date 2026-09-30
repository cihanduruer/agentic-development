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
        [Description("Focused subject keywords, up to 256 characters. Every term must match the knowledge passage.")]
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
        int skip,
        CancellationToken cancellationToken);
}

public sealed class AzureKnowledgeSearchRepository(SearchClient searchClient) : IKnowledgeSearchRepository
{
    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(
        string query,
        string revision,
        int maximumResults,
        int skip,
        CancellationToken cancellationToken)
    {
        var response = await searchClient.SearchAsync<KnowledgeSearchHit>(
            query,
            CreateSearchOptions(revision, maximumResults, skip),
            cancellationToken);
        var documents = new List<KnowledgeSearchHit>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            documents.Add(result.Document);
        }

        return documents;
    }

    public static SearchOptions CreateSearchOptions(string revision, int maximumResults, int skip = 0)
    {
        if (maximumResults is < 1 or > KnowledgeSearchService.MaximumResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResults),
                $"Search result count must be between 1 and {KnowledgeSearchService.MaximumResults}.");
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Search offset cannot be negative.");
        }

        var escapedRevision = revision.Replace("'", "''", StringComparison.Ordinal);
        return new SearchOptions
        {
            Filter = $"{nameof(KnowledgeSearchHit.Revision)} eq '{escapedRevision}'",
            SearchMode = SearchMode.All,
            QueryType = SearchQueryType.Simple,
            SearchFields = { nameof(KnowledgeSearchHit.Content) },
            Size = maximumResults,
            Skip = skip,
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
    public const int CandidatePageSize = 5;
    public const int MaximumCandidateScan = 25;
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
        var passages = new List<KnowledgePassage>();
        var incompleteEvidence = false;
        var scanned = 0;
        while (passages.Count < MaximumResults && scanned < MaximumCandidateScan)
        {
            var pageSize = Math.Min(CandidatePageSize, MaximumCandidateScan - scanned);
            var documents = await repository.SearchAsync(
                string.Join(' ', queryTerms),
                normalizedRevision,
                pageSize,
                scanned,
                cancellationToken);
            scanned += documents.Count;
            if (documents.Count == 0)
            {
                break;
            }

            foreach (var document in documents
                .Where(document =>
                    string.Equals(document.Revision, normalizedRevision, StringComparison.Ordinal) &&
                    IsCanonicalKnowledgePath(document.Path) &&
                    queryTerms.All(term => ContainsWholeTerm(document.Content, term))))
            {
                var passage = ToPassage(document, queryTerms);
                if (passage is null)
                {
                    incompleteEvidence = true;
                    continue;
                }

                passages.Add(passage);
                if (passages.Count == MaximumResults)
                {
                    break;
                }
            }

            if (documents.Count < pageSize)
            {
                break;
            }
        }

        return passages.Count == 0
            ? NoEvidence(
                normalizedRevision,
                incompleteEvidence
                    ? "incomplete_evidence"
                    : scanned >= MaximumCandidateScan
                        ? "scan_limit_reached"
                        : "no_evidence",
                incompleteEvidence
                    ? $"Matching knowledge evidence at revision {normalizedRevision} cannot be represented within the passage bounds."
                    : scanned >= MaximumCandidateScan
                    ? $"Knowledge evidence scan limit reached at revision {normalizedRevision}; no complete result can be claimed."
                    : $"No matching knowledge evidence exists at revision {normalizedRevision}.")
            : new KnowledgeSearchResult(
                normalizedRevision,
                true,
                "evidence_found",
                $"Found {passages.Count} knowledge passage(s) at revision {normalizedRevision}.",
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

    private static KnowledgePassage? ToPassage(KnowledgeSearchHit document, IReadOnlyList<string> queryTerms)
    {
        var truncated = document.Content.Length > MaximumPassageLength;
        var passage = truncated ? BuildRelevantExcerpt(document.Content, queryTerms) : document.Content;
        if (passage is null)
        {
            return null;
        }

        if (!queryTerms.All(term => ContainsWholeTerm(passage, term)))
        {
            return null;
        }

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

    private static string? BuildRelevantExcerpt(string content, IReadOnlyList<string> queryTerms)
    {
        var occurrences = queryTerms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(term => (Term: term, Index: FindWholeTermOccurrence(content, term)))
            .Where(match => match.Index >= 0)
            .OrderBy(match => match.Index)
            .ToArray();
        if (occurrences.Length == 0)
        {
            return content[..MaximumPassageLength];
        }

        var first = occurrences.Min(match => match.Index);
        var last = occurrences.Max(match => match.Index + match.Term.Length);
        if (last - first <= MaximumPassageLength)
        {
            var start = Math.Clamp(
                (first + last) / 2 - MaximumPassageLength / 2,
                Math.Max(0, last - MaximumPassageLength),
                Math.Min(first, content.Length - MaximumPassageLength));
            return content.Substring(start, MaximumPassageLength);
        }

        const int separatorLength = 5;
        var requiredLength = occurrences.Sum(match => match.Term.Length) +
            (occurrences.Length - 1) * separatorLength;
        if (requiredLength > MaximumPassageLength)
        {
            return null;
        }

        var contextBudget = MaximumPassageLength - requiredLength;
        var contextPerSnippet = contextBudget / occurrences.Length;
        var remainingContext = contextBudget % occurrences.Length;
        var snippets = occurrences.Select((match, index) =>
        {
            var snippetContext = contextPerSnippet + (index < remainingContext ? 1 : 0);
            var leftContext = snippetContext / 2;
            var rightContext = snippetContext - leftContext;
            var start = Math.Max(0, match.Index - leftContext);
            var end = Math.Min(content.Length, match.Index + match.Term.Length + rightContext);
            start = Math.Min(start, match.Index);
            end = Math.Max(end, match.Index + match.Term.Length);

            return content[start..end];
        });
        var excerpt = string.Join("\n...\n", snippets);
        return excerpt.Length <= MaximumPassageLength ? excerpt : null;
    }

    private static int FindWholeTermOccurrence(string text, string term)
    {
        var occurrence = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (occurrence >= 0)
        {
            var matchEnd = occurrence + term.Length;
            if ((occurrence == 0 || !char.IsLetterOrDigit(text[occurrence - 1])) &&
                (matchEnd == text.Length || !char.IsLetterOrDigit(text[matchEnd])))
            {
                return occurrence;
            }

            occurrence = text.IndexOf(term, occurrence + 1, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }

    private static bool ContainsWholeTerm(string text, string term)
    {
        var occurrence = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (occurrence >= 0)
        {
            var matchEnd = occurrence + term.Length;
            var startsTerm = occurrence == 0 || !char.IsLetterOrDigit(text[occurrence - 1]);
            var endsTerm = matchEnd == text.Length || !char.IsLetterOrDigit(text[matchEnd]);
            if (startsTerm && endsTerm)
            {
                return true;
            }

            occurrence = text.IndexOf(term, occurrence + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
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
