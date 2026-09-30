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

if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length < KnowledgeMcpToken.MinimumLength)
{
    throw new InvalidOperationException("KnowledgeMcp:AccessToken must contain at least 32 characters.");
}

builder.Services.AddSingleton<TokenCredential>(_ =>
    new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
builder.Services.AddSingleton(services => new SearchClient(
    endpoint,
    KnowledgeSearchService.IndexName,
    services.GetRequiredService<TokenCredential>()));
builder.Services.AddSingleton<AzureKnowledgeSearchRepository>();
builder.Services.AddSingleton<KnowledgeSearchService>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<KnowledgeMcpTools>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp") &&
        !KnowledgeMcpToken.IsAuthorized(context.Request.Headers.Authorization, accessToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});
app.MapMcp("/mcp");

await app.RunAsync();
