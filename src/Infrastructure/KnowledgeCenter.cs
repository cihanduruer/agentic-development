using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using OpenAI;
using OpenAI.Embeddings;

namespace AgenticHotelBooking.Infrastructure;

public static class KnowledgeCenterSchema
{
    public const int EmbeddingDimensions = 3072;
    public const string VectorProfileName = "knowledge-vector-profile";
    public const string VectorAlgorithmName = "knowledge-hnsw";
    public const string SemanticConfigurationName = "knowledge-semantic";

    public static SearchIndex BuildIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        return new SearchIndex(indexName, new FieldBuilder().Build(typeof(KnowledgeCenterDocument)))
        {
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgorithmName) },
                Profiles = { new VectorSearchProfile(VectorProfileName, VectorAlgorithmName) }
            },
            SemanticSearch = new SemanticSearch
            {
                DefaultConfigurationName = SemanticConfigurationName,
                Configurations =
                {
                    new SemanticConfiguration(
                        SemanticConfigurationName,
                        new SemanticPrioritizedFields
                        {
                            TitleField = new SemanticField(nameof(KnowledgeCenterDocument.Title)),
                            ContentFields = { new SemanticField(nameof(KnowledgeCenterDocument.Content)) }
                        })
                }
            }
        };
    }
}

public sealed class KnowledgeCenterDocument
{
    [SimpleField(IsKey = true)]
    public required string Id { get; init; }
    [SimpleField(IsFilterable = true, IsFacetable = true)]
    public required string Area { get; init; }
    [SimpleField(IsFilterable = true, IsFacetable = true)]
    public required string SourceType { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string SourcePath { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Revision { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Owner { get; init; }
    [SimpleField(IsFilterable = true, IsSortable = true)]
    public required string LastReviewed { get; init; }
    [SearchableField]
    public required string Title { get; init; }
    [SearchableField(AnalyzerName = LexicalAnalyzerName.Values.EnMicrosoft)]
    public required string Content { get; init; }
    [SimpleField]
    public required string ContentHash { get; init; }
    [SimpleField(IsFilterable = true, IsSortable = true)]
    public int ChunkIndex { get; init; }
    [VectorSearchField(
        VectorSearchDimensions = KnowledgeCenterSchema.EmbeddingDimensions,
        VectorSearchProfileName = KnowledgeCenterSchema.VectorProfileName)]
    public required IReadOnlyList<float> ContentVector { get; init; }
}

public sealed record KnowledgeCenterChunk(
    string Id,
    string Area,
    string SourceType,
    string SourcePath,
    string Revision,
    string Owner,
    string LastReviewed,
    string Title,
    string Content,
    string ContentHash,
    int ChunkIndex);

public static class KnowledgeCenterChunker
{
    public const int MaxChunkCharacters = 6000;

    public static IReadOnlyList<KnowledgeCenterChunk> FromMarkdown(
        string area,
        string repositoryPath,
        string markdown,
        string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);

        var normalizedPath = repositoryPath.Replace('\\', '/');
        var chunks = new List<KnowledgeCenterChunk>();
        foreach (var section in KnowledgeDocumentChunker.Chunk(repositoryPath, markdown, revision))
        {
            var parts = SplitLongContent(section.Content).ToArray();
            for (var part = 0; part < parts.Length; part++)
            {
                var title = parts.Length == 1
                    ? section.Title
                    : string.Create(CultureInfo.InvariantCulture, $"{section.Title} (part {part + 1} of {parts.Length})");
                var index = chunks.Count;
                chunks.Add(new KnowledgeCenterChunk(
                    StableId(area, normalizedPath, index),
                    area,
                    "markdown",
                    normalizedPath,
                    revision,
                    section.Owner,
                    section.LastReviewed,
                    title,
                    parts[part],
                    Hash(parts[part]),
                    index));
            }
        }

        return chunks;
    }

    public static IReadOnlyList<KnowledgeCenterChunk> FromSections(
        string area,
        string sourcePath,
        string sourceType,
        string owner,
        string lastReviewed,
        string revision,
        IEnumerable<ExtractedSection> sections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var chunks = new List<KnowledgeCenterChunk>();
        foreach (var section in sections.Where(section => !string.IsNullOrWhiteSpace(section.Text)))
        {
            var parts = SplitLongContent(section.Text.Trim()).ToArray();
            for (var part = 0; part < parts.Length; part++)
            {
                var title = parts.Length == 1
                    ? section.Title
                    : string.Create(CultureInfo.InvariantCulture, $"{section.Title} (part {part + 1} of {parts.Length})");
                var index = chunks.Count;
                chunks.Add(new KnowledgeCenterChunk(
                    StableId(area, sourcePath, index),
                    area,
                    sourceType,
                    sourcePath,
                    revision,
                    owner,
                    lastReviewed,
                    title,
                    parts[part],
                    Hash(parts[part]),
                    index));
            }
        }

        return chunks;
    }

    public static IEnumerable<string> SplitLongContent(string content)
    {
        if (content.Length <= MaxChunkCharacters)
        {
            yield return content;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var paragraph in content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > MaxChunkCharacters)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            if (paragraph.Length > MaxChunkCharacters)
            {
                for (var start = 0; start < paragraph.Length; start += MaxChunkCharacters)
                {
                    yield return paragraph[start..Math.Min(paragraph.Length, start + MaxChunkCharacters)];
                }

                continue;
            }

            current.Append(paragraph).Append("\n\n");
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    private static string StableId(string area, string path, int chunkIndex) =>
        Hash(string.Create(CultureInfo.InvariantCulture, $"{area}\n{path}\n{chunkIndex}"));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class AzureOpenAiEmbedder(Uri openAiEndpoint, string deployment, TokenCredential credential)
{
    private const int BatchSize = 16;
    private readonly Lazy<EmbeddingClient> embeddingClient = new(() =>
    {
#pragma warning disable OPENAI001
        var client = new OpenAIClient(
            new System.ClientModel.Primitives.BearerTokenPolicy(credential, "https://ai.azure.com/.default"),
            new OpenAIClientOptions { Endpoint = BuildV1Endpoint(openAiEndpoint) });
#pragma warning restore OPENAI001
        return client.GetEmbeddingClient(deployment);
    });

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        var vectors = new List<ReadOnlyMemory<float>>(inputs.Count);
        for (var start = 0; start < inputs.Count; start += BatchSize)
        {
            var batch = inputs.Skip(start).Take(BatchSize).ToArray();
            var response = await embeddingClient.Value.GenerateEmbeddingsAsync(batch, cancellationToken: cancellationToken);
            vectors.AddRange(response.Value.Select(embedding => embedding.ToFloats()));
        }

        return vectors;
    }

    private static Uri BuildV1Endpoint(Uri endpoint) =>
        endpoint.AbsolutePath.TrimEnd('/').EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : new UriBuilder(endpoint) { Path = $"{endpoint.AbsolutePath.TrimEnd('/')}/openai/v1/" }.Uri;
}

public sealed class KnowledgeCenterIndexer(
    Uri searchEndpoint,
    string indexName,
    TokenCredential credential,
    AzureOpenAiEmbedder embedder)
{
    public async Task<int> MigrateAsync(
        IReadOnlyList<KnowledgeCenterChunk> chunks,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            return 0;
        }

        var vectors = await embedder.EmbedAsync(
            chunks.Select(chunk => $"{chunk.Title}\n\n{chunk.Content}").ToArray(),
            cancellationToken);
        if (vectors.Any(vector => vector.Length != KnowledgeCenterSchema.EmbeddingDimensions))
        {
            throw new InvalidDataException(
                $"The embedding deployment must return {KnowledgeCenterSchema.EmbeddingDimensions} dimensions.");
        }

        var indexClient = new SearchIndexClient(searchEndpoint, credential);
        await indexClient.CreateOrUpdateIndexAsync(
            KnowledgeCenterSchema.BuildIndex(indexName),
            allowIndexDowntime: false,
            cancellationToken: cancellationToken);

        var documents = chunks.Select((chunk, position) => new KnowledgeCenterDocument
        {
            Id = chunk.Id,
            Area = chunk.Area,
            SourceType = chunk.SourceType,
            SourcePath = chunk.SourcePath,
            Revision = chunk.Revision,
            Owner = chunk.Owner,
            LastReviewed = chunk.LastReviewed,
            Title = chunk.Title,
            Content = chunk.Content,
            ContentHash = chunk.ContentHash,
            ChunkIndex = chunk.ChunkIndex,
            ContentVector = vectors[position].ToArray()
        }).ToArray();

        var response = await indexClient.GetSearchClient(indexName)
            .MergeOrUploadDocumentsAsync(documents, cancellationToken: cancellationToken);
        var failures = response.Value.Results.Where(result => !result.Succeeded).ToArray();
        if (failures.Length > 0)
        {
            throw new RequestFailedException(
                $"Azure AI Search rejected {failures.Length} knowledge center chunks: " +
                string.Join(", ", failures.Select(result => $"{result.Key}: {result.ErrorMessage}")));
        }

        return documents.Length;
    }
}
