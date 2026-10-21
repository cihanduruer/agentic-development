using AgenticHotelBooking.Infrastructure;
using Azure.Identity;
using Azure.Search.Documents;

// Archive the existing index without changing it. Existing durable records are never overwritten.
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
var pairs = args.Where(argument => !string.Equals(argument, "--dry-run", StringComparison.OrdinalIgnoreCase)).ToArray();
if (pairs.Length % 2 != 0 || pairs.Where((_, index) => index % 2 == 0).Any(argument => !argument.StartsWith("--", StringComparison.Ordinal)))
{
    throw new ArgumentException("Expected --search-endpoint URL --storage-endpoint URL [--index NAME] [--dry-run].");
}
var arguments = pairs.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
var searchEndpoint = arguments.GetValueOrDefault("--search-endpoint") ?? throw new ArgumentException("--search-endpoint is required.");
var storageEndpoint = arguments.GetValueOrDefault("--storage-endpoint") ?? throw new ArgumentException("--storage-endpoint is required.");
var credential = new DefaultAzureCredential();
var client = new SearchClient(new Uri(searchEndpoint), arguments.GetValueOrDefault("--index") ?? "hotel-knowledge-center", credential);
var store = new BlobKnowledgeDocumentStore(new KnowledgeStorageOptions(storageEndpoint), credential);
var documents = await KnowledgeArchive.ReadLegacyAsync(client, CancellationToken.None);
foreach (var document in documents)
{
    Console.WriteLine($"{document.Path}: {document.Sections.Count} blocks, revision {document.Revision}");
}
if (dryRun)
{
    Console.WriteLine($"Dry run: {documents.Count} documents. No writes.");
    return;
}
await using var lease = await store.LockAsync(CancellationToken.None);
foreach (var document in documents)
{
    if (await store.GetAsync(document.Path, lease.Token) is null)
    {
        await store.SaveAsync(document, lease.Token);
    }
    var retained = await store.GetAsync(document.Path, lease.Token);
    if (retained is null || retained.Path != document.Path ||
        retained.Revision == document.Revision && !retained.Sections.SequenceEqual(document.Sections))
    {
        throw new InvalidDataException($"Archive verification failed for {document.Path}.");
    }
}
Console.WriteLine($"Verified {documents.Count} retained documents. Legacy binary originals were not in the index and have not been fabricated.");

if (arguments.TryGetValue("--root", out var root))
{
    var revision = arguments.GetValueOrDefault("--revision")
        ?? throw new ArgumentException("--revision is required when importing repository originals.");
    foreach (var path in new[] { "docs/knowledge/product.md", "docs/knowledge/domain.md", "docs/knowledge/architecture.md" })
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, path), lease.Token);
        var current = await store.GetAsync(path, lease.Token);
        if (current?.Original is not null && current.Original.SequenceEqual(bytes))
        {
            Console.WriteLine($"Original already verified: {path}");
            continue;
        }
        var chunks = KnowledgeCenterChunker.FromMarkdown("knowledge", path, System.Text.Encoding.UTF8.GetString(bytes), revision);
        if (chunks.Count == 0)
        {
            throw new InvalidDataException($"No readable knowledge in {path}.");
        }
        var record = new KnowledgeDocument(path, "markdown", revision,
            current?.Owner ?? KnowledgeApproval.ApprovedOwner, chunks[0].LastReviewed,
            chunks.Select(chunk => new ExtractedSection(chunk.Title, chunk.Content)).ToArray(), bytes);
        await store.SaveAsync(record, lease.Token);
        var retained = await store.GetAsync(path, lease.Token);
        if (retained?.Original is null || !retained.Original.SequenceEqual(bytes) ||
            !retained.Sections.SequenceEqual(record.Sections))
        {
            throw new InvalidDataException($"Original verification failed for {path}.");
        }
        Console.WriteLine($"Verified original and extracted sections: {path}, revision {revision}");
    }
}
