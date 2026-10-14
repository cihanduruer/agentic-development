using AgenticHotelBooking.Infrastructure;
using Azure.Identity;

// One-time migration of repository knowledge into the Azure AI Search knowledge center.
// Source files stay in the repository and are never modified. Re-running prunes chunks of shortened or
// removed files within the migrated area; the admin page's uploads area is never touched.
// By default the hotel-site files (product, domain, architecture) are migrated; use --include to override.
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
var arguments = args
    .Where(argument => !string.Equals(argument, "--dry-run", StringComparison.OrdinalIgnoreCase))
    .Chunk(2)
    .Where(pair => pair.Length == 2)
    .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

var root = arguments.GetValueOrDefault("--root") ?? throw new ArgumentException("--root is required.");
var revision = arguments.GetValueOrDefault("--revision") ?? throw new ArgumentException("--revision is required.");
var area = arguments.GetValueOrDefault("--area") ?? "hotel-site";
var files = (arguments.GetValueOrDefault("--include")
        ?? "docs/knowledge/product.md,docs/knowledge/domain.md,docs/knowledge/architecture.md")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var chunks = files
    .SelectMany(file => KnowledgeCenterChunker.FromMarkdown(
        area,
        file,
        File.ReadAllText(Path.Combine(root, file)),
        revision))
    .ToArray();

foreach (var group in chunks.GroupBy(chunk => chunk.SourcePath))
{
    Console.WriteLine($"{group.Key}: {group.Count()} chunks, {group.Sum(chunk => chunk.Content.Length)} characters");
}

if (dryRun)
{
    Console.WriteLine($"Dry run: {chunks.Length} chunks would be migrated to area '{area}'. Nothing was sent to Azure.");
    return;
}

var searchEndpoint = arguments.GetValueOrDefault("--search-endpoint")
    ?? throw new ArgumentException("--search-endpoint is required.");
var openAiEndpoint = arguments.GetValueOrDefault("--openai-endpoint")
    ?? throw new ArgumentException("--openai-endpoint is required.");
var embeddingDeployment = arguments.GetValueOrDefault("--embedding-deployment") ?? "text-embedding-3-large";
var index = arguments.GetValueOrDefault("--index") ?? "hotel-knowledge-center";

var credential = new DefaultAzureCredential();
var migrated = await new KnowledgeCenterIndexer(
        new Uri(searchEndpoint),
        index,
        credential,
        new AzureOpenAiEmbedder(new Uri(openAiEndpoint), embeddingDeployment, credential))
    .MigrateAsync(chunks, CancellationToken.None);
Console.WriteLine($"Migrated {migrated.Migrated} chunks into index '{index}' (area '{area}', revision {revision}); removed {migrated.Pruned} stale chunks.");
