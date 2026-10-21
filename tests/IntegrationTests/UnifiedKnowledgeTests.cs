using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgenticHotelBooking.Infrastructure;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticHotelBooking.IntegrationTests;

public sealed class UnifiedKnowledgeTests
{
    private const string McpToken = "fixture-read-only-token-not-a-real-secret-123456789";
    private const string ManagementToken = "fixture-management-token-not-a-real-secret-987654321";

    [Fact]
    public async Task McpRequiresItsOwnTokenAndDiscoversOnlyReadOnlyTools()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        using var anonymous = await CallAsync(client, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ManagementToken);
        using var wrongPurpose = await CallAsync(client, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPurpose.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", McpToken);
        using var initialization = await CallAsync(client, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "fixture", version = "1" }
        });
        initialization.EnsureSuccessStatusCode();
        using var response = await CallAsync(client, "tools/list");
        var payload = await ReadRpcAsync(response);
        var tools = payload.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["list_knowledge", "read_knowledge", "search_knowledge"],
            tools.Select(tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.All(tools, tool => Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
    }

    [Fact]
    public async Task McpHidesPendingDocumentsPinsRevisionsAndBoundsContent()
    {
        using var fixture = new Fixture();
        using var client = fixture.McpClient();
        var listing = await ToolAsync(client, "list_knowledge", new { });
        var documents = JsonSerializer.Deserialize<JsonElement>(listing.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!)
            .GetProperty("documents");
        Assert.Equal("docs/knowledge/product.md", Assert.Single(documents.EnumerateArray()).GetProperty("path").GetString());
        var read = await ToolAsync(client, "read_knowledge", new { path = "docs/knowledge/product.md", revision = "revision-1", limit = 12 });
        var body = JsonSerializer.Deserialize<JsonElement>(read.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(12, body.GetProperty("content").GetString()!.Length);
        Assert.Equal(12, body.GetProperty("nextOffset").GetInt32());
        var changed = await ToolAsync(client, "read_knowledge", new { path = "docs/knowledge/product.md", revision = "outdated" });
        Assert.True(changed.GetProperty("result").GetProperty("isError").GetBoolean());
        var pending = await ToolAsync(client, "read_knowledge", new { path = "uploads/pending.txt" });
        Assert.True(pending.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task RepositoryOriginDeletionIsDurableAndRebuildDoesNotResurrectIt()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        using var deleted = await client.DeleteAsync("/api/knowledge/documents?path=docs/knowledge/product.md");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(fixture.Store.Documents.ContainsKey("docs/knowledge/product.md"));
        using var rebuilt = await client.PostAsync("/api/knowledge/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, rebuilt.StatusCode);
        Assert.DoesNotContain(fixture.Index.Documents.Values, document => document["SourcePath"]?.ToString() == "docs/knowledge/product.md");
        Assert.Contains(fixture.Index.Documents.Values, document => document["SourcePath"]?.ToString() == "uploads/pending.txt");
        using var mcp = fixture.McpClient();
        var read = await ToolAsync(mcp, "read_knowledge", new { path = "docs/knowledge/product.md" });
        Assert.True(read.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task OriginalSurvivesIndexLossAndRebuildPreservesApprovalAndRevision()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        fixture.Index.Documents.Clear();
        using var original = await client.GetAsync("/api/knowledge/original?path=docs/knowledge/product.md");
        Assert.Equal("# Hotel\nRooms cannot be double booked.", await original.Content.ReadAsStringAsync());
        Assert.Equal(2, fixture.Store.Documents.Count);
        Assert.Same(fixture.Store, fixture.App.Services.GetRequiredService<IKnowledgeDocumentStore>());
        var inventory = await client.GetFromJsonAsync<KnowledgeBlock[]>("/api/knowledge/blocks");
        Assert.Equal(2, inventory!.Length);
        using var rebuilt = await client.PostAsync("/api/knowledge/rebuild", null);
        rebuilt.EnsureSuccessStatusCode();
        Assert.Equal(2, fixture.Index.Documents.Count);
        Assert.Equal("revision-1", fixture.Store.Documents["docs/knowledge/product.md"].Revision);
        Assert.True(fixture.Store.Documents["docs/knowledge/product.md"].Approved);
        Assert.False(fixture.Store.Documents["uploads/pending.txt"].Approved);
    }

    [Fact]
    public async Task FailedIndexingRetainsOriginalPendingDocumentAndRebuildRepairsIt()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        fixture.Index.FailWrites = true;
        using var response = await client.PostAsJsonAsync("/api/knowledge/text", new { title = "New rules", text = "Breakfast starts at 7." });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var retained = fixture.Store.Documents["uploads/New rules.txt"];
        Assert.False(retained.Approved);
        Assert.Equal("Breakfast starts at 7.", Encoding.UTF8.GetString(retained.Original!));
        fixture.Index.FailWrites = false;
        using var rebuild = await client.PostAsync("/api/knowledge/rebuild", null);
        rebuild.EnsureSuccessStatusCode();
        using var approval = await client.PostAsync("/api/knowledge/documents/approve?path=uploads/New%20rules.txt", null);
        approval.EnsureSuccessStatusCode();
        Assert.True(fixture.Store.Documents["uploads/New rules.txt"].Approved);
    }

    [Fact]
    public async Task FailedDeletionTombstonesDocumentBeforeIndexRemovalAndCanBeRetried()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        fixture.Index.FailWrites = true;
        using var response = await client.DeleteAsync("/api/knowledge/documents?path=docs/knowledge/product.md");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(fixture.Store.Documents["docs/knowledge/product.md"].Deleted);
        using var mcp = fixture.McpClient();
        var read = await ToolAsync(mcp, "read_knowledge", new { path = "docs/knowledge/product.md" });
        Assert.True(read.GetProperty("result").GetProperty("isError").GetBoolean());
        fixture.Index.FailWrites = false;
        using var retry = await client.DeleteAsync("/api/knowledge/documents?path=docs/knowledge/product.md");
        retry.EnsureSuccessStatusCode();
        Assert.False(fixture.Store.Documents.ContainsKey("docs/knowledge/product.md"));
    }

    [Fact]
    public async Task AzureManagementRejectsAnonymousAndMcpTokens()
    {
        using var fixture = new Fixture(production: true);
        using var client = fixture.App.CreateClient();
        using var anonymous = await client.GetAsync("/api/knowledge/blocks");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Knowledge-Token", McpToken);
        using var wrongPurpose = await client.DeleteAsync("/api/knowledge/documents?path=docs/knowledge/product.md");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPurpose.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Knowledge-Token");
        client.DefaultRequestHeaders.Add("X-Knowledge-Token", ManagementToken);
        using var authorized = await client.GetAsync("/api/knowledge/blocks");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using var mcp = fixture.McpClient();
        using var tools = await CallAsync(mcp, "tools/list");
        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);
    }

    [Fact]
    public async Task HybridSearchRejectsPendingAndSupersededCanonicalRevisions()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        using var rebuilt = await client.PostAsync("/api/knowledge/rebuild", null);
        rebuilt.EnsureSuccessStatusCode();
        using var mcp = fixture.McpClient();
        var result = await ToolAsync(mcp, "search_knowledge", new { query = "Rooms", limit = 5 });
        var matches = JsonSerializer.Deserialize<KnowledgeSource[]>(
            result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("docs/knowledge/product.md", Assert.Single(matches!).Path);
        Assert.Equal("revision-1", matches![0].Revision);
        fixture.Store.Documents["docs/knowledge/product.md"] =
            fixture.Store.Documents["docs/knowledge/product.md"] with { Revision = "replacement-not-yet-indexed" };
        var stale = await ToolAsync(mcp, "search_knowledge", new { query = "Rooms", limit = 5 });
        var staleMatches = JsonSerializer.Deserialize<KnowledgeSource[]>(
            stale.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Empty(staleMatches!);
    }

    [Theory]
    [InlineData("list_knowledge", "{\"limit\":101}")]
    [InlineData("read_knowledge", "{\"path\":\"docs/knowledge/product.md\",\"limit\":20001}")]
    [InlineData("read_knowledge", "{\"path\":\"../secrets\",\"limit\":1}")]
    [InlineData("search_knowledge", "{\"query\":\"Rooms\",\"limit\":11}")]
    public async Task McpRejectsOutOfBoundsRequests(string name, string arguments)
    {
        using var fixture = new Fixture();
        using var mcp = fixture.McpClient();
        var result = await ToolAsync(mcp, name, JsonSerializer.Deserialize<JsonElement>(arguments));
        Assert.True(result.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    private static Task<HttpResponseMessage> CallAsync(HttpClient client, string method, object? parameters = null) =>
        client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } });

    private static async Task<JsonElement> ReadRpcAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            text = text.Split('\n').First(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        }
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static async Task<JsonElement> ToolAsync(HttpClient client, string name, object arguments)
    {
        using var response = await CallAsync(client, "tools/call", new { name, arguments });
        return await ReadRpcAsync(response);
    }

    private sealed class Fixture : IDisposable
    {
        public MemoryStore Store { get; } = new();
        public IndexHandler Index { get; } = new();
        public WebApplicationFactory<Program> App { get; }

        public Fixture(bool production = false)
        {
            Store.Documents.Add("docs/knowledge/product.md", new KnowledgeDocument(
                "docs/knowledge/product.md", "markdown", "revision-1", KnowledgeApproval.ApprovedOwner, "2026-10-20",
                [new ExtractedSection("Hotel", "Rooms cannot be double booked.")],
                Encoding.UTF8.GetBytes("# Hotel\nRooms cannot be double booked.")));
            Store.Documents.Add("uploads/pending.txt", new KnowledgeDocument(
                "uploads/pending.txt", "text", "revision-2", KnowledgeApproval.PendingOwner, "2026-10-20",
                [new ExtractedSection("Unapproved", "Do not retrieve this.")]));
            foreach (var document in Store.Documents.Values)
            {
                Index.Documents.Add(document.Path, new Dictionary<string, object?>
                {
                    ["Id"] = document.Path,
                    ["SourcePath"] = document.Path,
                    ["Owner"] = document.Owner
                });
            }
            var options = new WorkItemChatOptions("https://model.example/", "chat", "embedding", "https://search.example/",
                "hotel-knowledge-center", "organization", "project", false);
            var searchClient = new SearchClient(new Uri(options.SearchEndpoint!), options.SearchIndex, new AzureKeyCredential("fixture"),
                new SearchClientOptions { Transport = new HttpClientTransport(new HttpClient(Index)), Retry = { MaxRetries = 0 } });
            var schemaClient = new SearchIndexClient(new Uri(options.SearchEndpoint!), new AzureKeyCredential("fixture"),
                new SearchClientOptions { Transport = new HttpClientTransport(new HttpClient(Index)), Retry = { MaxRetries = 0 } });
            App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(production ? "Production" : "Development");
                builder.UseSetting("KnowledgeAccess:McpToken", McpToken);
                builder.UseSetting("KnowledgeAccess:ManagementToken", ManagementToken);
                builder.UseSetting("OperationsAuth:Authority", "https://login.example/tenant/v2.0");
                builder.UseSetting("OperationsAuth:Audience", "fixture");
                builder.UseSetting("OperationsAuth:RequiredRole", "Operations.Ingest");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IKnowledgeDocumentStore>(Store);
                    services.AddSingleton(new KnowledgeCenterSearch(options, new NoNetworkCredential(), searchClient, Store, new Embedder()));
                    services.AddSingleton(new KnowledgeUploadService(new KnowledgeUploadOptions(true), options,
                        new NoNetworkCredential(), searchClient, Store, new Embedder(), schemaClient));
                });
            });
        }

        public HttpClient McpClient()
        {
            var client = App.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", McpToken);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            return client;
        }

        public void Dispose()
        {
            App.Dispose();
            Index.Dispose();
        }
    }

    private sealed class MemoryStore : IKnowledgeDocumentStore
    {
        public Dictionary<string, KnowledgeDocument> Documents { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocument>>(Documents.Values.ToArray());
        public Task<KnowledgeDocument?> GetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Documents.GetValueOrDefault(path));
        public Task SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken)
        {
            Documents[document.Path] = document;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            Documents.Remove(path);
            return Task.CompletedTask;
        }
        public Task<IKnowledgeWriteLease> LockAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IKnowledgeWriteLease>(new Lease(cancellationToken));
    }

    private sealed class Lease(CancellationToken token) : IKnowledgeWriteLease
    {
        public CancellationToken Token => token;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Embedder : IKnowledgeEmbedder
    {
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(inputs.Select(_ =>
                new ReadOnlyMemory<float>(new float[KnowledgeCenterSchema.EmbeddingDimensions])).ToArray());
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Live credentials are forbidden in fixtures.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Live credentials are forbidden in fixtures.");
    }

    private sealed class IndexHandler : HttpMessageHandler
    {
        public Dictionary<string, Dictionary<string, object?>> Documents { get; } = new(StringComparer.Ordinal);
        public bool FailWrites { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { error = new { message = "Index must be rebuilt" } }) };
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.TryGetProperty("fields", out _))
            {
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(body.RootElement.Clone()) };
            }
            if (body.RootElement.TryGetProperty("value", out var actions))
            {
                if (FailWrites)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { error = new { message = "Fixture indexing failure" } }) };
                }
                var receipts = new List<object>();
                foreach (var action in actions.EnumerateArray())
                {
                    var id = action.GetProperty("Id").GetString()!;
                    if (action.GetProperty("@search.action").GetString() == "delete")
                    {
                        Documents.Remove(id);
                    }
                    else
                    {
                        if (!Documents.TryGetValue(id, out var document))
                        {
                            document = new Dictionary<string, object?>();
                            Documents.Add(id, document);
                        }
                        foreach (var property in action.EnumerateObject().Where(property => property.Name != "@search.action"))
                        {
                            document[property.Name] = property.Value.Clone();
                        }
                    }
                    receipts.Add(new { key = id, status = true, statusCode = 200 });
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { value = receipts }) };
            }
            IEnumerable<Dictionary<string, object?>> documents = Documents.Values;
            if (body.RootElement.TryGetProperty("filter", out var filter) && filter.GetString() is { } expression)
            {
                const string prefix = "SourcePath eq '";
                if (expression.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var path = expression[prefix.Length..^1].Replace("''", "'", StringComparison.Ordinal);
                    documents = documents.Where(document => document["SourcePath"]?.ToString() == path);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { value = documents.ToArray() }) };
        }
    }
}
