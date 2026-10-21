using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgenticHotelBooking.Infrastructure;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticHotelBooking.IntegrationTests;

public sealed class KnowledgeManagementTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public KnowledgeManagementTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("")]
    [InlineData("?path=")]
    [InlineData("?path=%20")]
    public async Task PreviewRequiresAnExplicitSource(string query)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/knowledge/preview" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PreviewRejectsOversizedPaths()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/knowledge/preview?path=" + new string('a', 1025));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PreviewReportsMissingConfiguration()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/knowledge/preview?path=docs/knowledge/product.md");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task WritesAreDisabledByDefaultAndCapabilitiesMatch()
    {
        using var client = factory.CreateClient();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/knowledge/capabilities");
        Assert.False(capabilities.GetProperty("canManageUploads").GetBoolean());
        using var response = await client.DeleteAsync("/api/knowledge/documents?path=uploads/notes.txt");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PreviewReturnsCompleteTextInBlockOrderForOnlyTheSelectedSource()
    {
        using var handler = new SearchFixtureHandler();
        using var app = CreateApp(handler);
        using var client = app.CreateClient();

        var preview = await client.GetFromJsonAsync<KnowledgeBlock[]>("/api/knowledge/preview?path=uploads/notes.txt");
        Assert.NotNull(preview);
        Assert.Equal([0, 1], preview.Select(block => block.ChunkIndex));
        Assert.All(preview, block => Assert.Equal("uploads/notes.txt", block.Path));
        Assert.Equal(SearchFixtureHandler.FullContent, preview[0].Content);
        Assert.True(preview[0].Content!.Length > preview[0].Excerpt.Length);
        Assert.Equal("SourcePath eq 'uploads/notes.txt'", handler.LastFilter);

        var listing = await client.GetFromJsonAsync<KnowledgeBlock[]>("/api/knowledge/blocks");
        Assert.NotNull(listing);
        Assert.Equal(4, listing.Length);
        Assert.All(listing, block => Assert.Null(block.Content));
    }

    [Fact]
    public async Task RepositoryOriginDocumentsCanBeDeletedLikeEveryOtherDocument()
    {
        using var handler = new SearchFixtureHandler();
        using var app = CreateApp(handler);
        using var client = app.CreateClient();
        var preview = await client.GetFromJsonAsync<KnowledgeBlock[]>("/api/knowledge/preview?path=docs/knowledge/product.md");
        Assert.Equal("Repository knowledge", Assert.Single(preview!).Content);
        using var response = await client.DeleteAsync("/api/knowledge/documents?path=docs/knowledge/product.md");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["product"], handler.DeletedIds);
    }

    [Fact]
    public async Task PreviewEscapesSourcePathsAndReportsMissingSources()
    {
        using var handler = new SearchFixtureHandler();
        using var app = CreateApp(handler);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/knowledge/preview?path=" + Uri.EscapeDataString("uploads/it's.txt"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("SourcePath eq 'uploads/it''s.txt'", handler.LastFilter);
    }

    [Fact]
    public async Task DeletionRemovesAllSelectedUploadBlocksAndPreservesOtherSources()
    {
        using var handler = new SearchFixtureHandler();
        using var app = CreateApp(handler);
        using var client = app.CreateClient();
        using var response = await client.DeleteAsync("/api/knowledge/documents?path=uploads/notes.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("removed").GetInt32());
        Assert.Equal(["notes-0", "notes-1"], handler.DeletedIds.Order(StringComparer.Ordinal));

        var listing = await client.GetFromJsonAsync<KnowledgeBlock[]>("/api/knowledge/blocks");
        Assert.NotNull(listing);
        Assert.Equal(2, listing.Length);
        Assert.DoesNotContain(listing, block => block.Path == "uploads/notes.txt");
        Assert.Contains(listing, block => block.Path == "uploads/other.txt");
        Assert.Contains(listing, block => block.Path == "docs/knowledge/product.md");

        using var repeated = await client.DeleteAsync("/api/knowledge/documents?path=uploads/notes.txt");
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        Assert.Equal(1, handler.DeleteCalls);
    }

    [Fact]
    public async Task FailedDeleteIsNotReportedAsSuccessAndDoesNotRemoveSources()
    {
        using var handler = new SearchFixtureHandler { FailDelete = true };
        using var app = CreateApp(handler);
        using var client = app.CreateClient();
        using var response = await client.DeleteAsync("/api/knowledge/documents?path=uploads/notes.txt");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(handler.DeletedIds);
        Assert.Equal(4, handler.Documents.Count);
    }

    [Fact]
    public async Task SearchFailureIsAnExplicitPreviewError()
    {
        using var handler = new SearchFixtureHandler { FailSearch = true };
        using var app = CreateApp(handler);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/knowledge/preview?path=uploads/notes.txt");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private WebApplicationFactory<Program> CreateApp(SearchFixtureHandler handler)
    {
        var options = new WorkItemChatOptions(
            "https://model.example/", "chat", "embedding", "https://search.example/",
            "hotel-knowledge-center", "ai-enabled-ado-org", "sample-project", false);
        var search = new SearchClient(new Uri(options.SearchEndpoint!), options.SearchIndex, new AzureKeyCredential("fixture-only"),
            new SearchClientOptions
            {
                Transport = new HttpClientTransport(new HttpClient(handler)),
                Retry = { MaxRetries = 0 }
            });
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(new KnowledgeCenterSearch(options, new NoNetworkCredential(), search));
            services.AddSingleton(new KnowledgeUploadService(new KnowledgeUploadOptions(true), options, new NoNetworkCredential(), search));
        }));
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests must not request live credentials.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests must not request live credentials.");
    }

    private sealed class SearchFixtureHandler : HttpMessageHandler
    {
        public static string FullContent { get; } = new string('x', 800) + "\n<script>Untrusted text, not HTML</script>";
        public List<Dictionary<string, object>> Documents { get; } =
        [
            Document("notes-1", "uploads/notes.txt", 1, "Second block"),
            Document("product", "docs/knowledge/product.md", 0, "Repository knowledge"),
            Document("notes-0", "uploads/notes.txt", 0, FullContent),
            Document("other", "uploads/other.txt", 0, "Other upload")
        ];
        public List<string> DeletedIds { get; } = [];
        public int DeleteCalls { get; private set; }
        public string? LastFilter { get; private set; }
        public bool FailDelete { get; init; }
        public bool FailSearch { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.TryGetProperty("value", out var actions))
            {
                DeleteCalls++;
                if (FailDelete)
                {
                    return Json(HttpStatusCode.ServiceUnavailable, new { error = new { code = "Unavailable", message = "Fixture failure" } });
                }

                var ids = actions.EnumerateArray().Select(action => action.GetProperty("Id").GetString()!).ToArray();
                Assert.All(actions.EnumerateArray(), action => Assert.Equal("delete", action.GetProperty("@search.action").GetString()));
                DeletedIds.AddRange(ids);
                Documents.RemoveAll(document => ids.Contains((string)document["Id"], StringComparer.Ordinal));
                return Json(HttpStatusCode.OK, new { value = ids.Select(id => new { key = id, status = true, statusCode = 200 }) });
            }

            if (FailSearch)
            {
                return Json(HttpStatusCode.ServiceUnavailable, new { error = new { code = "Unavailable", message = "Fixture failure" } });
            }

            LastFilter = body.RootElement.TryGetProperty("filter", out var filter) ? filter.GetString() : null;
            var selected = Documents.Where(document => LastFilter is null ||
                LastFilter == KnowledgeIndexOperations.EqualsFilter("SourcePath", (string)document["SourcePath"]));
            return Json(HttpStatusCode.OK, new { value = selected });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        private static Dictionary<string, object> Document(string id, string path, int index, string content) => new()
        {
            ["@search.score"] = 1.0,
            ["Id"] = id,
            ["SourcePath"] = path,
            ["Title"] = "Block " + index,
            ["Area"] = path.StartsWith("uploads/", StringComparison.Ordinal) ? "uploads" : "product",
            ["SourceType"] = "txt",
            ["ChunkIndex"] = index,
            ["Content"] = content,
            ["LastReviewed"] = "2026-10-20",
            ["Owner"] = KnowledgeApproval.ApprovedOwner
        };
    }
}
