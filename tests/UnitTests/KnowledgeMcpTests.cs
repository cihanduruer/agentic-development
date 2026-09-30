using System.Reflection;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AgenticHotelBooking.Infrastructure;
using AgenticHotelBooking.Tools.KnowledgeMcp;
using Azure.Search.Documents.Models;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using IndexedKnowledgeDocument = AgenticHotelBooking.Infrastructure.KnowledgeSearchDocument;
using McpSearchHit = AgenticHotelBooking.Tools.KnowledgeMcp.KnowledgeSearchHit;

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
        Assert.Null(result.Revision);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task McpToolReturnsAnExplicitNoEvidenceResultWhenRevisionIsOmitted()
    {
        var repository = new FakeKnowledgeSearchRepository();
        var tool = new KnowledgeMcpTools(new KnowledgeSearchService(repository));

        using var response = JsonDocument.Parse(
            await tool.SearchKnowledgeAsync("application security", null, CancellationToken.None));

        Assert.Equal("revision_required", response.RootElement.GetProperty("Status").GetString());
        Assert.False(response.RootElement.GetProperty("HasEvidence").GetBoolean());
        Assert.Equal(0, repository.CallCount);
        var revisionParameter = typeof(KnowledgeMcpTools)
            .GetMethod(nameof(KnowledgeMcpTools.SearchKnowledgeAsync))!
            .GetParameters()[1];
        Assert.NotNull(revisionParameter.GetCustomAttribute<RequiredAttribute>());
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
            $"Revision eq '{Revision}'",
            options.Filter);
        Assert.DoesNotContain("startswith", options.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(KnowledgeSearchService.MaximumResults, options.Size);
        Assert.Contains(nameof(McpSearchHit.Content), options.Select);
        Assert.Contains(nameof(McpSearchHit.Revision), options.Select);
        Assert.Contains(nameof(McpSearchHit.Path), options.Select);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AzureKnowledgeSearchRepository.CreateSearchOptions(Revision, KnowledgeSearchService.MaximumResults + 1));
    }

    [Fact]
    public void SearchHitMatchesTheIndexerDocumentFields()
    {
        var indexedFields = typeof(IndexedKnowledgeDocument)
            .GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(field => field.Name, StringComparer.Ordinal);
        var retrievedFields = typeof(McpSearchHit)
            .GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(field => field.Name, StringComparer.Ordinal);

        Assert.Equal(indexedFields, retrievedFields);
    }

    [Theory]
    [InlineData("docs/knowledge/security.md", true)]
    [InlineData("docs/knowledge/nested/security.md", true)]
    [InlineData("docs/knowledge/../secrets.md", false)]
    [InlineData("docs/knowledge/..\\secrets.md", false)]
    [InlineData("docs/other/security.md", false)]
    [InlineData("docs/knowledge/readme.txt", false)]
    [InlineData("docs/knowledge/readme.MD", false)]
    public async Task SearchReturnsOnlyCanonicalMarkdownPaths(string path, bool expectedEvidence)
    {
        var service = new KnowledgeSearchService(
            new FakeKnowledgeSearchRepository(Document(path, Revision)));

        var result = await service.SearchAsync("security policy", Revision, CancellationToken.None);

        Assert.Equal(expectedEvidence, result.HasEvidence);
        if (expectedEvidence)
        {
            Assert.Equal(path, Assert.Single(result.Passages).Path);
        }
        else
        {
            Assert.Equal("no_evidence", result.Status);
            Assert.Empty(result.Passages);
        }
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
        Assert.False(KnowledgeMcpToken.IsValidAccessToken(new string('x', KnowledgeMcpToken.MaximumLength + 1)));
        Assert.False(KnowledgeMcpToken.IsValidAccessToken(new string('é', KnowledgeMcpToken.MinimumLength)));
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/health")]
    [InlineData("/unmapped")]
    public async Task McpEndpointMiddlewareRejectsRequestsWithoutTheBearerToken(string path)
    {
        var nextCalled = false;
        var middleware = new KnowledgeMcpAuthorizationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            "0123456789abcdef0123456789abcdef");
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    private static McpSearchHit Document(string path, string revision) => new()
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

    private sealed class FakeKnowledgeSearchRepository(params McpSearchHit[] documents)
        : IKnowledgeSearchRepository
    {
        public int CallCount { get; private set; }
        public string? Query { get; private set; }
        public string? Revision { get; private set; }
        public int MaximumResults { get; private set; }

        public Task<IReadOnlyList<McpSearchHit>> SearchAsync(
            string query,
            string revision,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Query = query;
            Revision = revision;
            MaximumResults = maximumResults;
            return Task.FromResult<IReadOnlyList<McpSearchHit>>(documents);
        }
    }
}
