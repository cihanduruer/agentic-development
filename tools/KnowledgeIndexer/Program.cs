using AgenticHotelBooking.Infrastructure;
using Azure.Identity;

var arguments = args
    .Chunk(2)
    .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
var root = arguments.GetValueOrDefault("--root")
    ?? throw new ArgumentException("--root is required.");
var endpoint = arguments.GetValueOrDefault("--endpoint")
    ?? throw new ArgumentException("--endpoint is required.");
var revision = arguments.GetValueOrDefault("--revision")
    ?? throw new ArgumentException("--revision is required.");
var index = arguments.GetValueOrDefault("--index") ?? "knowledge";

var documents = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
    .OrderBy(path => path, StringComparer.Ordinal)
    .SelectMany(path => KnowledgeDocumentChunker.Chunk(
        Path.GetRelativePath(Directory.GetCurrentDirectory(), path),
        File.ReadAllText(path),
        revision))
    .ToArray();

await new AzureKnowledgeIndexer(new Uri(endpoint), index, new DefaultAzureCredential())
    .IndexAsync(documents, CancellationToken.None);
Console.WriteLine($"Indexed {documents.Length} knowledge chunks at revision {revision}.");
