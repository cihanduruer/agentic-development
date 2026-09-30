using AgenticHotelBooking.Tools.KnowledgeMcp;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
var searchEndpoint = builder.Configuration["KnowledgeMcp:SearchEndpoint"];
var accessToken = builder.Configuration["KnowledgeMcp:AccessToken"];
if (!Uri.TryCreate(searchEndpoint, UriKind.Absolute, out var endpoint) ||
    endpoint.Scheme != Uri.UriSchemeHttps)
{
    throw new InvalidOperationException("KnowledgeMcp:SearchEndpoint must be an HTTPS URI.");
}

if (!KnowledgeMcpToken.IsValidAccessToken(accessToken))
{
    throw new InvalidOperationException(
        "KnowledgeMcp:AccessToken must be 32 to 128 URL-safe ASCII characters.");
}
var bearerToken = accessToken!;

builder.Services.AddSingleton<TokenCredential>(_ =>
    new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
builder.Services.AddSingleton(services => new SearchClient(
    endpoint,
    KnowledgeSearchService.IndexName,
    services.GetRequiredService<TokenCredential>()));
builder.Services.AddSingleton<IKnowledgeSearchRepository, AzureKnowledgeSearchRepository>();
builder.Services.AddSingleton<KnowledgeSearchService>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<KnowledgeMcpTools>();

var app = builder.Build();
app.UseMiddleware<KnowledgeMcpAuthorizationMiddleware>(bearerToken);
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapMcp("/mcp");

await app.RunAsync();
