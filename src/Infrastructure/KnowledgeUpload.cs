using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using Word = DocumentFormat.OpenXml.Wordprocessing;
using Xl = DocumentFormat.OpenXml.Spreadsheet;

namespace AgenticHotelBooking.Infrastructure;

public sealed record KnowledgeUploadOptions(bool Enabled);

public sealed record ExtractedSection(string Title, string Text);

public sealed record ExtractedDocument(string SourceType, IReadOnlyList<ExtractedSection> Sections);

public sealed record UploadResult(string Path, int Blocks, int Characters, int Replaced);

public sealed record TextUploadRequest(string Title, string Text);

public sealed class KnowledgeUploadDisabledException()
    : InvalidOperationException("Uploading knowledge is disabled in this environment.");

public static class KnowledgeApproval
{
    public const string PendingOwner = "Uploaded - pending approval";
    public const string ApprovedOwner = "Uploaded - approved";

    // Excludes uploads nobody has approved yet from retrieval.
    public const string SearchFilter = "Owner ne 'Uploaded - pending approval'";
}

public static class KnowledgeUploadLimits
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxTextCharacters = 200_000;
    public const int MaxExtractedCharacters = 400_000;
    public const int MaxBlocks = 150;
    public const int MaxPdfPages = 300;
    public const int MaxSpreadsheetRows = 5_000;
    public const int MaxZipEntries = 2_000;
    public const long MaxUncompressedBytes = 100L * 1024 * 1024;
    public const int MaxTitleCharacters = 120;
    public const string UploadsPrefix = "uploads/";
}

public static class UploadedDocumentExtractor
{
    private static readonly string[] TextExtensions = [".txt", ".md", ".markdown", ".csv"];

    public static readonly string[] SupportedExtensions = [.. TextExtensions, ".pdf", ".docx", ".xlsx"];

    public static ExtractedDocument Extract(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Unsupported file type. Use {string.Join(", ", SupportedExtensions)}.");
        }

        if (content.Length == 0)
        {
            throw new InvalidDataException("The file is empty.");
        }

        if (content.Length > KnowledgeUploadLimits.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"The file is larger than {KnowledgeUploadLimits.MaxFileBytes / (1024 * 1024)} MB.");
        }

        try
        {
            var title = Path.GetFileNameWithoutExtension(fileName) ?? "Document";
            var document = extension switch
            {
                ".pdf" => ExtractPdf(content),
                ".docx" => ExtractWord(content, title),
                ".xlsx" => ExtractSpreadsheet(content),
                ".md" or ".markdown" => new ExtractedDocument("markdown", SplitMarkdown(DecodeText(content), title)),
                _ => new ExtractedDocument(extension.TrimStart('.'), [new ExtractedSection(title, DecodeText(content))])
            };
            return Validate(document);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "The file could not be read. It may be corrupted, encrypted or password protected.");
        }
    }

    public static ExtractedDocument ExtractText(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > KnowledgeUploadLimits.MaxTitleCharacters)
        {
            throw new InvalidDataException($"A title of 1-{KnowledgeUploadLimits.MaxTitleCharacters} characters is required.");
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > KnowledgeUploadLimits.MaxTextCharacters)
        {
            throw new InvalidDataException($"Text must be 1-{KnowledgeUploadLimits.MaxTextCharacters:N0} characters.");
        }

        return Validate(new ExtractedDocument("text", SplitMarkdown(text, title.Trim())));
    }

    public static IReadOnlyList<ExtractedSection> SplitMarkdown(string text, string fallbackTitle)
    {
        var sections = new List<ExtractedSection>();
        var title = fallbackTitle;
        var body = new StringBuilder();

        void Flush()
        {
            if (body.ToString().Trim().Length > 0)
            {
                sections.Add(new ExtractedSection(title, body.ToString().Trim()));
            }

            body.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') && trimmed.TrimStart('#').StartsWith(' ') && trimmed.Length - trimmed.TrimStart('#').Length <= 3)
            {
                Flush();
                title = trimmed.TrimStart('#').Trim();
                continue;
            }

            body.AppendLine(line);
        }

        Flush();
        return sections;
    }

    private static ExtractedDocument Validate(ExtractedDocument document)
    {
        var sections = document.Sections.Where(section => !string.IsNullOrWhiteSpace(section.Text)).ToArray();
        if (sections.Length == 0)
        {
            throw new InvalidDataException(
                "No readable text was found. Scanned PDFs and images without a text layer are not supported.");
        }

        if (sections.Sum(section => section.Text.Length) > KnowledgeUploadLimits.MaxExtractedCharacters)
        {
            throw new InvalidDataException(
                $"The document contains more than {KnowledgeUploadLimits.MaxExtractedCharacters:N0} characters of text.");
        }

        return document with { Sections = sections };
    }

    private static string DecodeText(byte[] content)
    {
        if (content.Take(8_000).Contains((byte)0))
        {
            throw new InvalidDataException("The file looks binary, not text.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Text files must be UTF-8 encoded.");
        }
    }

    private static ExtractedDocument ExtractPdf(byte[] content)
    {
        if (content.Length < 5 || Encoding.ASCII.GetString(content, 0, 5) != "%PDF-")
        {
            throw new InvalidDataException("The file is not a valid PDF.");
        }

        using var document = PdfDocument.Open(content);
        if (document.NumberOfPages > KnowledgeUploadLimits.MaxPdfPages)
        {
            throw new InvalidDataException($"PDFs can have at most {KnowledgeUploadLimits.MaxPdfPages} pages.");
        }

        var sections = new List<ExtractedSection>();
        var buffer = new StringBuilder();
        var firstPage = 0;
        var lastPage = 0;

        void Flush()
        {
            if (buffer.ToString().Trim().Length > 0)
            {
                sections.Add(new ExtractedSection(
                    firstPage == lastPage
                        ? string.Create(CultureInfo.InvariantCulture, $"Page {firstPage}")
                        : string.Create(CultureInfo.InvariantCulture, $"Pages {firstPage}-{lastPage}"),
                    buffer.ToString().Trim()));
            }

            buffer.Clear();
        }

        foreach (var page in document.GetPages())
        {
            var text = page.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (buffer.Length > 0 && buffer.Length + text.Length > KnowledgeCenterChunker.MaxChunkCharacters)
            {
                Flush();
            }

            if (buffer.Length == 0)
            {
                firstPage = page.Number;
            }

            lastPage = page.Number;
            buffer.AppendLine(text).AppendLine();
        }

        Flush();
        return new ExtractedDocument("pdf", sections);
    }

    private static ExtractedDocument ExtractWord(byte[] content, string fallbackTitle)
    {
        EnsureZipPackage(content, "word/document.xml", "Word");
        using var stream = new MemoryStream(content);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("The Word document has no content.");

        var sections = new List<ExtractedSection>();
        var title = fallbackTitle;
        var text = new StringBuilder();

        void Flush()
        {
            if (text.ToString().Trim().Length > 0)
            {
                sections.Add(new ExtractedSection(title, text.ToString().Trim()));
            }

            text.Clear();
        }

        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Word.Paragraph paragraph:
                    var line = paragraph.InnerText.Trim();
                    var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    if (line.Length > 0 && style is not null &&
                        (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                         style.Equals("Title", StringComparison.OrdinalIgnoreCase)))
                    {
                        Flush();
                        title = line.Length > KnowledgeUploadLimits.MaxTitleCharacters
                            ? line[..KnowledgeUploadLimits.MaxTitleCharacters]
                            : line;
                    }
                    else if (line.Length > 0)
                    {
                        text.AppendLine(line).AppendLine();
                    }

                    break;
                case Word.Table table:
                    foreach (var row in table.Elements<Word.TableRow>())
                    {
                        text.AppendLine(string.Join(
                            " | ",
                            row.Elements<Word.TableCell>().Select(cell => cell.InnerText.Trim())));
                    }

                    text.AppendLine();
                    break;
                default:
                    break;
            }
        }

        Flush();
        return new ExtractedDocument("docx", sections);
    }

    private static ExtractedDocument ExtractSpreadsheet(byte[] content)
    {
        EnsureZipPackage(content, "xl/workbook.xml", "Excel");
        using var stream = new MemoryStream(content);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The workbook has no content.");
        var shared = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<Xl.SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];

        var sections = new List<ExtractedSection>();
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Xl.Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } relationshipId ||
                workbook.GetPartById(relationshipId) is not WorksheetPart worksheet)
            {
                continue;
            }

            var lines = new List<string>();
            var rows = worksheet.Worksheet?.Descendants<Xl.Row>()
                .Take(KnowledgeUploadLimits.MaxSpreadsheetRows + 1).ToArray() ?? [];
            if (rows.Length > KnowledgeUploadLimits.MaxSpreadsheetRows)
            {
                throw new InvalidDataException(
                    $"Sheet '{sheet.Name?.Value}' has more than {KnowledgeUploadLimits.MaxSpreadsheetRows:N0} rows. Split the workbook so no knowledge is silently dropped.");
            }

            foreach (var row in rows)
            {
                var values = row.Elements<Xl.Cell>()
                    .Select(cell => CellText(cell, shared))
                    .Where(value => value.Length > 0)
                    .ToArray();
                if (values.Length > 0)
                {
                    lines.Add(string.Join(" | ", values));
                }
            }

            sections.Add(new ExtractedSection(sheet.Name?.Value ?? "Sheet", string.Join('\n', lines)));
        }

        return new ExtractedDocument("xlsx", sections);
    }

    private static string CellText(Xl.Cell cell, string[] shared)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        var type = cell.DataType?.Value;
        if (type == Xl.CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index < shared.Length)
        {
            return shared[index].Trim();
        }

        if (type == Xl.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText.Trim() ?? string.Empty;
        }

        if (type == Xl.CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        return raw.Trim();
    }

    private static void EnsureZipPackage(byte[] content, string requiredEntry, string kind)
    {
        if (content.Length < 4 || content[0] != 0x50 || content[1] != 0x4B || content[2] != 0x03 || content[3] != 0x04)
        {
            throw new InvalidDataException($"The file is not a valid {kind} document.");
        }

        using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        if (archive.Entries.Count > KnowledgeUploadLimits.MaxZipEntries ||
            archive.Entries.Sum(entry => entry.Length) > KnowledgeUploadLimits.MaxUncompressedBytes)
        {
            throw new InvalidDataException("The document package is too large to import safely.");
        }

        if (!archive.Entries.Any(entry => entry.FullName.Equals(requiredEntry, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"The file is not a valid {kind} document.");
        }
    }
}

public sealed class KnowledgeUploadService(
    KnowledgeUploadOptions upload,
    WorkItemChatOptions chat,
    TokenCredential credential,
    SearchClient? client = null,
    IKnowledgeDocumentStore? store = null,
    IKnowledgeEmbedder? embeddingClient = null,
    SearchIndexClient? schemaClient = null)
{
    private readonly Lazy<IKnowledgeEmbedder> embedder = new(() => embeddingClient ?? new AzureOpenAiEmbedder(
        new Uri(chat.Endpoint ?? throw new InvalidOperationException("WorkItemChat:Endpoint is required.")),
        chat.EmbeddingDeployment,
        credential));

    private readonly Lazy<SearchClient> searchClient = new(() => client ?? new SearchClient(
        new Uri(chat.SearchEndpoint ?? throw new InvalidOperationException("WorkItemChat:SearchEndpoint is required.")),
        chat.SearchIndex,
        credential));

    public bool Enabled => upload.Enabled;

    public static string SourcePathFor(string displayName)
    {
        var name = Path.GetFileName((displayName ?? string.Empty).Replace('\\', '/'));
        var cleaned = new string(name
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ' ' ? character : '_')
            .ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return KnowledgeUploadLimits.UploadsPrefix + "untitled";
        }

        // Names that had to be altered or shortened get a hash of the original so different files cannot collide.
        if (!string.Equals(cleaned, name, StringComparison.Ordinal) || cleaned.Length > 100)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 10)
            {
                extension = extension[..10];
            }

            var stem = cleaned[..^Path.GetExtension(cleaned).Length];
            if (stem.Length > 80)
            {
                stem = stem[..80];
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..8];
            cleaned = $"{stem}-{hash}{extension}";
        }

        return KnowledgeUploadLimits.UploadsPrefix + cleaned;
    }

    public static bool IsUploadPath(string? path) => IsSourcePath(path);

    public static bool IsSourcePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 &&
        !path.StartsWith('/') && !path.Contains('\\') &&
        !path.Split('/').Any(part => part is "" or "." or "..") &&
        !path.Any(char.IsControl);

    public async Task<UploadResult> IngestAsync(
        string displayName,
        ExtractedDocument document,
        CancellationToken cancellationToken,
        byte[]? original = null)
    {
        if (!upload.Enabled)
        {
            throw new KnowledgeUploadDisabledException();
        }

        await using var lease = store is null ? null : await store.LockAsync(cancellationToken);
        cancellationToken = lease?.Token ?? cancellationToken;
        var path = SourcePathFor(displayName);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var record = new KnowledgeDocument(
            path, document.SourceType, Guid.NewGuid().ToString("N"),
            KnowledgeApproval.PendingOwner, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            document.Sections, original);
        if (store is not null)
        {
            await store.SaveAsync(record, cancellationToken);
        }

        return await IndexDocumentAsync(record, cancellationToken);
    }

    private async Task<UploadResult> IndexDocumentAsync(KnowledgeDocument record, CancellationToken cancellationToken)
    {
        var path = record.Path;
        var chunks = KnowledgeCenterChunker.FromSections(
            "knowledge",
            path,
            record.SourceType,
            record.Owner,
            record.LastReviewed,
            record.Revision,
            record.Sections);
        if (chunks.Count == 0)
        {
            throw new InvalidDataException("No readable text was found.");
        }

        if (chunks.Count > KnowledgeUploadLimits.MaxBlocks)
        {
            throw new InvalidDataException(
                $"The content would create {chunks.Count} blocks; the limit is {KnowledgeUploadLimits.MaxBlocks}. Split it into smaller files.");
        }

        // Fail closed: before any slow or fallible work, nothing from the previous version stays retrievable.
        var existingIds = await FindIdsAsync(path, cancellationToken);
        await KnowledgeIndexOperations.SetOwnerAsync(
            searchClient.Value, existingIds, KnowledgeApproval.PendingOwner, cancellationToken);

        var vectors = await embedder.Value.EmbedAsync(
            chunks.Select(chunk => $"{chunk.Title}\n\n{chunk.Content}").ToArray(),
            cancellationToken);
        if (vectors.Count != chunks.Count ||
            vectors.Any(vector => vector.Length != KnowledgeCenterSchema.EmbeddingDimensions))
        {
            throw new InvalidDataException("The embedding service returned invalid knowledge vectors.");
        }
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

        var response = await searchClient.Value.MergeOrUploadDocumentsAsync(documents, cancellationToken: cancellationToken);
        var failures = response.Value.Results.Where(result => !result.Succeeded).ToArray();
        if (failures.Length > 0)
        {
            throw new RequestFailedException(
                $"Azure AI Search rejected {failures.Length} uploaded blocks: " +
                string.Join(", ", failures.Select(result => $"{result.Key}: {result.ErrorMessage}")));
        }

        var keep = documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
        var stale = existingIds.Where(id => !keep.Contains(id)).ToArray();
        await DeleteIdsAsync(stale, cancellationToken);
        return new UploadResult(path, documents.Length, chunks.Sum(chunk => chunk.Content.Length), stale.Length);
    }

    public async Task<int> RemoveAsync(string path, CancellationToken cancellationToken)
    {
        if (!upload.Enabled)
        {
            throw new KnowledgeUploadDisabledException();
        }

        if (!IsSourcePath(path))
        {
            throw new ArgumentException("A valid document path is required.", nameof(path));
        }

        await using var lease = store is null ? null : await store.LockAsync(cancellationToken);
        cancellationToken = lease?.Token ?? cancellationToken;
        var document = store is null ? null : await store.GetAsync(path, cancellationToken);
        if (document is not null)
        {
            await store!.SaveAsync(document with { Deleted = true }, cancellationToken);
        }

        var ids = await FindIdsAsync(path, cancellationToken);
        await DeleteIdsAsync(ids, cancellationToken);
        if (store is not null)
        {
            await store.DeleteAsync(path, cancellationToken);
        }
        return Math.Max(ids.Count, document is null ? 0 : 1);
    }

    public async Task<int> ApproveAsync(string path, CancellationToken cancellationToken)
    {
        if (!upload.Enabled)
        {
            throw new KnowledgeUploadDisabledException();
        }

        if (!IsSourcePath(path))
        {
            throw new ArgumentException("A valid document path is required.", nameof(path));
        }

        await using var lease = store is null ? null : await store.LockAsync(cancellationToken);
        cancellationToken = lease?.Token ?? cancellationToken;
        var document = store is null ? null : await store.GetAsync(path, cancellationToken);
        if (store is not null && (document is null || document.Deleted))
        {
            return 0;
        }
        var ids = await FindIdsAsync(path, cancellationToken);
        if (ids.Count == 0)
        {
            return 0;
        }

        await KnowledgeIndexOperations.SetOwnerAsync(
            searchClient.Value, ids, KnowledgeApproval.ApprovedOwner, cancellationToken);
        if (document is not null)
        {
            await store!.SaveAsync(document with { Owner = KnowledgeApproval.ApprovedOwner }, cancellationToken);
        }
        return ids.Count;
    }

    public async Task<int> RebuildAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            throw new KnowledgeUploadDisabledException();
        }
        if (store is null)
        {
            throw new InvalidOperationException("Durable knowledge storage is required for rebuilding.");
        }

        await using var lease = await store.LockAsync(cancellationToken);
        var documents = await store.ListAsync(lease.Token);
        var schema = schemaClient ?? new SearchIndexClient(
            new Uri(chat.SearchEndpoint ?? throw new InvalidOperationException("WorkItemChat:SearchEndpoint is required.")), credential);
        try
        {
            await schema.GetIndexAsync(chat.SearchIndex, lease.Token);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            await schema.CreateIndexAsync(KnowledgeCenterSchema.BuildIndex(chat.SearchIndex), lease.Token);
        }
        var count = 0;
        foreach (var document in documents.Where(document => !document.Deleted))
        {
            var result = await IndexDocumentAsync(document, lease.Token);
            count += result.Blocks;
        }

        var keep = documents.Where(document => !document.Deleted)
            .SelectMany(document => KnowledgeCenterChunker.FromSections(
                "knowledge", document.Path, document.SourceType, document.Owner,
                document.LastReviewed, document.Revision, document.Sections))
            .Select(chunk => chunk.Id).ToHashSet(StringComparer.Ordinal);
        var existing = await KnowledgeIndexOperations.FindIdsAsync(searchClient.Value, null, lease.Token);
        await DeleteIdsAsync(existing.Where(id => !keep.Contains(id)).ToArray(), lease.Token);
        return count;
    }

    private Task<List<string>> FindIdsAsync(string path, CancellationToken cancellationToken) =>
        KnowledgeIndexOperations.FindIdsAsync(
            searchClient.Value,
            KnowledgeIndexOperations.EqualsFilter(nameof(KnowledgeCenterDocument.SourcePath), path),
            cancellationToken);

    private Task DeleteIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
        KnowledgeIndexOperations.DeleteIdsAsync(searchClient.Value, ids, cancellationToken);
}
