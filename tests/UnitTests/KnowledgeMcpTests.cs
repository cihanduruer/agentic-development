using System.Reflection;
using AgenticHotelBooking.Infrastructure;
using AgenticHotelBooking.Tools.KnowledgeMcp;
using Azure.Search.Documents.Models;
using ModelContextProtocol.Server;

namespace AgenticHotelBooking.UnitTests;

public sealed class KnowledgeMcpTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task SearchReturnsCitedEvidenceForOnlyTheRequestedRevision()
    {
        var repository = new FakeKnowledgeSearchRepository(
            Document("docs/knowledge/security.md", Revision),
            Document("docs/knowledge/architecture.md", new string('a', 40)),
            Document("docs/knowledge/../../secrets.md", Revision));
        var service = new KnowledgeSearchService(repository);

        var result = await service.SearchAsync("  managed identity  ", Revision.ToUpperInvariant(), CancellationToken.None);

        Assert.True(result.HasEvidence);
        Assert.Equal("evidence_found", result.Status);
        Assert.Equal(Revision, result.Revision);
        var passage = Assert.Single(result.Passages);
        Assert.Equal("docs/knowledge/security.md", passage.Path);
        Assert.Equal(
            $"https://github.com/cihanduruer/agentic-development/blob/{Revision}/docs/knowledge/security.md",
            passage.SourceLink);
        Assert.Equal("Security owner", passage.Owner);
        Assert.Equal("2026-09-30", passage.LastReviewed);
        Assert.Equal(new string('b', 64), passage.ContentHash);
        Assert.True(passage.PassageTruncated);
        Assert.Equal(KnowledgeSearchService.MaximumPassageLength, passage.Passage.Length);
        Assert.Equal("managed identity", repository.Query);
        Assert.Equal(Revision, repository.Revision);
        Assert.Equal(KnowledgeSearchService.MaximumResults, repository.MaximumResults);
    }

    [Fact]
    public async Task SearchReturnsExplicitNoEvidenceWhenRevisionHasNoHit()
    {
        var service = new KnowledgeSearchService(new FakeKnowledgeSearchRepository());

        var result = await service.SearchAsync("missing policy", Revision, CancellationToken.None);

        Assert.False(result.HasEvidence);
        Assert.Equal("no_evidence", result.Status);
        Assert.Equal(Revision, result.Revision);
        Assert.Empty(result.Passages);
        Assert.Contains(Revision, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "revision_required")]
    [InlineData("", "revision_required")]
    [InlineData("not-a-full-sha", "invalid_revision")]
    public async Task SearchRequiresAFullRevision(string? revision, string expectedStatus)
    {
        var repository = new FakeKnowledgeSearchRepository();
        var service = new KnowledgeSearchService(repository);

        var result = await service.SearchAsync("application security", revision, CancellationToken.None);

        Assert.False(result.HasEvidence);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task SearchRejectsEmptyAndOversizedQueriesBeforeCallingSearch()
    {
        var repository = new FakeKnowledgeSearchRepository();
        var service = new KnowledgeSearchService(repository);

        var empty = await service.SearchAsync(" ", Revision, CancellationToken.None);
        var oversized = await service.SearchAsync(
            new string('x', KnowledgeSearchService.MaximumQueryLength + 1),
            Revision,
            CancellationToken.None);

        Assert.Equal("query_required", empty.Status);
        Assert.Equal("query_too_long", oversized.Status);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public void AzureSearchFilterPinsRevisionAndCanonicalKnowledgePaths()
    {
        var options = AzureKnowledgeSearchRepository.CreateSearchOptions(Revision, KnowledgeSearchService.MaximumResults);

        Assert.Equal(
            $"Revision eq '{Revision}' and startswith(Path, 'docs/knowledge/')",
            options.Filter);
        Assert.Equal(KnowledgeSearchService.MaximumResults, options.Size);
        Assert.Contains(nameof(KnowledgeSearchDocument.Content), options.Select);
        Assert.Contains(nameof(KnowledgeSearchDocument.Revision), options.Select);
        Assert.Contains(nameof(KnowledgeSearchDocument.Path), options.Select);
    }

    [Fact]
    public void McpToolIsAdvertisedAsReadOnly()
    {
        var method = typeof(KnowledgeMcpTools).GetMethod(nameof(KnowledgeMcpTools.SearchKnowledgeAsync));
        var tool = method?.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(tool);
        Assert.Equal("search_knowledge", tool.Name);
        Assert.True(tool.ReadOnly);
        Assert.False(tool.Destructive);
    }

    [Fact]
    public void BearerTokenValidationRequiresAnExactLongConfiguredSecret()
    {
        const string token = "0123456789abcdef0123456789abcdef";

        Assert.True(KnowledgeMcpToken.IsAuthorized("Bearer " + token, token));
        Assert.False(KnowledgeMcpToken.IsAuthorized(null, token));
        Assert.False(KnowledgeMcpToken.IsAuthorized("Bearer " + "wrong-token", token));
        Assert.False(KnowledgeMcpToken.IsAuthorized("Bearer " + token, "short"));
    }

    private static KnowledgeSearchDocument Document(string path, string revision) => new()
    {
        Id = new string('c', 64),
        Path = path,
        Revision = revision,
        Owner = "Security owner",
        LastReviewed = "2026-09-30",
        Title = "Managed identity",
        Content = new string('x', KnowledgeSearchService.MaximumPassageLength + 1),
        ContentHash = new string('b', 64)
    };

    private sealed class FakeKnowledgeSearchRepository(params KnowledgeSearchDocument[] documents)
        : IKnowledgeSearchRepository
    {
        public int CallCount { get; private set; }
        public string? Query { get; private set; }
        public string? Revision { get; private set; }
        public int MaximumResults { get; private set; }

        public Task<IReadOnlyList<KnowledgeSearchDocument>> SearchAsync(
            string query,
            string revision,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Query = query;
            Revision = revision;
            MaximumResults = maximumResults;
            return Task.FromResult<IReadOnlyList<KnowledgeSearchDocument>>(documents);
        }
    }
}
