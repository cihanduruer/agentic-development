using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace AgenticHotelBooking.Infrastructure;

public sealed class KnowledgeSearchDocument
{
    [SimpleField(IsKey = true)]
    public required string Id { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Path { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Revision { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string Owner { get; init; }
    [SimpleField(IsFilterable = true)]
    public required string LastReviewed { get; init; }
    [SearchableField]
    public required string Title { get; init; }
    [SearchableField]
    public required string Content { get; init; }
    [SimpleField]
    public required string ContentHash { get; init; }
}

public static class KnowledgeDocumentChunker
{
    public static IReadOnlyList<KnowledgeSearchDocument> Chunk(
        string repositoryPath,
        string markdown,
        string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var parts = normalized.Split("---\n", 3, StringSplitOptions.None);
        if (parts.Length != 3 || parts[0].Length != 0)
        {
            throw new InvalidDataException($"{repositoryPath} must start with YAML metadata.");
        }

        var metadata = parts[1]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!metadata.TryGetValue("owner", out var owner) ||
            string.IsNullOrWhiteSpace(owner) ||
            !metadata.TryGetValue("last_reviewed", out var lastReviewed) ||
            !DateOnly.TryParseExact(
                lastReviewed,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new InvalidDataException(
                $"{repositoryPath} metadata requires a non-empty owner and a valid yyyy-MM-dd last_reviewed date.");
        }

        var sections = SplitSections(parts[2]);
        var normalizedPath = repositoryPath.Replace('\\', '/');
        return sections.Select((section, index) =>
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(section.Content))).ToLowerInvariant();
            var keyMaterial = $"{revision}\n{normalizedPath}\n{index}\n{hash}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
            return new KnowledgeSearchDocument
            {
                Id = id,
                Path = normalizedPath,
                Revision = revision,
                Owner = owner,
                LastReviewed = lastReviewed,
                Title = section.Title,
                Content = section.Content,
                ContentHash = hash
            };
        }).ToArray();
    }

    private static List<(string Title, string Content)> SplitSections(string markdown)
    {
        var sections = new List<(string Title, string Content)>();
        var title = "Overview";
        var content = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) && content.Length > 0)
            {
                sections.Add((title, content.ToString().Trim()));
                content.Clear();
                title = line[3..].Trim();
            }

            content.AppendLine(line);
        }

        if (content.Length > 0)
        {
            sections.Add((title, content.ToString().Trim()));
        }

        return sections;
    }
}

public sealed class AzureKnowledgeIndexer(
    Uri searchEndpoint,
    string indexName,
    TokenCredential credential)
{
    public async Task IndexAsync(
        IEnumerable<KnowledgeSearchDocument> documents,
        CancellationToken cancellationToken)
    {
        var indexClient = new SearchIndexClient(searchEndpoint, credential);
        var fields = new FieldBuilder().Build(typeof(KnowledgeSearchDocument));
        foreach (var field in fields)
        {
            field.IsFilterable = field.Name is nameof(KnowledgeSearchDocument.Revision)
                or nameof(KnowledgeSearchDocument.Path)
                or nameof(KnowledgeSearchDocument.Owner)
                or nameof(KnowledgeSearchDocument.LastReviewed);
        }

        await indexClient.CreateOrUpdateIndexAsync(
            new SearchIndex(indexName, fields),
            allowIndexDowntime: false,
            cancellationToken: cancellationToken);
        var searchClient = indexClient.GetSearchClient(indexName);
        var response = await searchClient.MergeOrUploadDocumentsAsync(documents, cancellationToken: cancellationToken);
        var failures = response.Value.Results.Where(result => !result.Succeeded).ToArray();
        if (failures.Length > 0)
        {
            throw new RequestFailedException(
                $"Azure AI Search rejected {failures.Length} knowledge chunks: " +
                string.Join(", ", failures.Select(result => $"{result.Key}: {result.ErrorMessage}")));
        }
    }
}
