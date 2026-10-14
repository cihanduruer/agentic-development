using System.IO.Compression;
using System.Text;
using AgenticHotelBooking.Infrastructure;
using Azure.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Word = DocumentFormat.OpenXml.Wordprocessing;
using Xl = DocumentFormat.OpenXml.Spreadsheet;

namespace AgenticHotelBooking.UnitTests;

public sealed class KnowledgeUploadTests
{
    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    [Fact]
    public void ReadsPlainTextAndCsvAsOneSection()
    {
        var text = UploadedDocumentExtractor.Extract("Guest notes.txt", Utf8("Guests like late check-out."));
        var csv = UploadedDocumentExtractor.Extract("rates.csv", Utf8("room,rate\nKing,189"));

        Assert.Equal("txt", text.SourceType);
        var section = Assert.Single(text.Sections);
        Assert.Equal("Guest notes", section.Title);
        Assert.Contains("late check-out", section.Text, StringComparison.Ordinal);
        Assert.Equal("csv", csv.SourceType);
    }

    [Fact]
    public void SplitsMarkdownOnHeadingsAndKeepsTheFallbackTitleForLeadingText()
    {
        var document = UploadedDocumentExtractor.Extract(
            "policy.md",
            Utf8("Intro line\n\n# Cancellation\nFree until 48 hours.\n\n## Fees\nNone."));

        Assert.Equal(["policy", "Cancellation", "Fees"], document.Sections.Select(section => section.Title));
        Assert.Contains("48 hours", document.Sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsTextFromARealPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddText("Cancellation policy for guests", 12, new PdfPoint(25, 700), font);
        builder.AddPage(595, 842).AddText("Second page about parking", 12, new PdfPoint(25, 700), font);

        var document = UploadedDocumentExtractor.Extract("policy.pdf", builder.Build());

        Assert.Equal("pdf", document.SourceType);
        var combined = string.Join(' ', document.Sections.Select(section => section.Text));
        Assert.Contains("Cancellation policy", combined, StringComparison.Ordinal);
        Assert.Contains("parking", combined, StringComparison.Ordinal);
        Assert.StartsWith("Page", document.Sections[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsHeadingsParagraphsAndTablesFromARealWordDocument()
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Word.Document(new Word.Body(
                new Word.Paragraph(
                    new Word.ParagraphProperties(new Word.ParagraphStyleId { Val = "Heading1" }),
                    new Word.Run(new Word.Text("Parking"))),
                new Word.Paragraph(new Word.Run(new Word.Text("Parking is free for guests."))),
                new Word.Table(new Word.TableRow(
                    new Word.TableCell(new Word.Paragraph(new Word.Run(new Word.Text("Car")))),
                    new Word.TableCell(new Word.Paragraph(new Word.Run(new Word.Text("EUR 0"))))))));
            main.Document.Save();
        }

        var document = UploadedDocumentExtractor.Extract("handbook.docx", stream.ToArray());

        var section = Assert.Single(document.Sections);
        Assert.Equal("Parking", section.Title);
        Assert.Contains("free for guests", section.Text, StringComparison.Ordinal);
        Assert.Contains("Car | EUR 0", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsEachSheetOfARealExcelWorkbookAsASection()
    {
        using var stream = new MemoryStream();
        using (var excel = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = excel.AddWorkbookPart();
            workbook.Workbook = new Xl.Workbook(new Xl.Sheets());
            var sheets = workbook.Workbook.GetFirstChild<Xl.Sheets>()!;
            foreach (var (name, room, rate) in new[] { ("Amsterdam", "Canal King", "189"), ("Rotterdam", "Harbor Studio", "149") })
            {
                var part = workbook.AddNewPart<WorksheetPart>();
                part.Worksheet = new Xl.Worksheet(new Xl.SheetData(new Xl.Row(
                    new Xl.Cell { DataType = Xl.CellValues.InlineString, InlineString = new Xl.InlineString(new Xl.Text(room)) },
                    new Xl.Cell { CellValue = new Xl.CellValue(rate) })));
                sheets.Append(new Xl.Sheet
                {
                    Id = workbook.GetIdOfPart(part),
                    SheetId = (uint)(sheets.ChildElements.Count + 1),
                    Name = name
                });
            }

            workbook.Workbook.Save();
        }

        var document = UploadedDocumentExtractor.Extract("rates.xlsx", stream.ToArray());

        Assert.Equal(["Amsterdam", "Rotterdam"], document.Sections.Select(section => section.Title));
        Assert.Equal("Canal King | 189", document.Sections[0].Text);
        Assert.Equal("Harbor Studio | 149", document.Sections[1].Text);
    }

    [Theory]
    [InlineData("report.exe")]
    [InlineData("notes")]
    [InlineData("archive.zip")]
    [InlineData("page.html")]
    public void RejectsUnsupportedFileTypes(string fileName)
    {
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract(fileName, Utf8("data")));
    }

    [Fact]
    public void RejectsEmptyOversizedBinaryAndNonUtf8Text()
    {
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("a.txt", []));
        Assert.Throws<InvalidDataException>(() =>
            UploadedDocumentExtractor.Extract("a.txt", new byte[KnowledgeUploadLimits.MaxFileBytes + 1]));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("a.txt", [0x41, 0x00, 0x42]));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("a.txt", [0xFF, 0xFE, 0x41, 0x42]));
    }

    [Fact]
    public void RejectsFilesWhoseContentDoesNotMatchTheirExtension()
    {
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("fake.pdf", Utf8("not a pdf at all")));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("fake.docx", Utf8("plain text")));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("fake.xlsx", Utf8("plain text")));
    }

    [Fact]
    public void RejectsAZipThatIsNotAWordOrExcelPackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open());
            writer.Write("hello");
        }

        var bytes = stream.ToArray();

        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("x.docx", bytes));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("x.xlsx", bytes));
    }

    [Fact]
    public void RejectsADocumentWithNoReadableText()
    {
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.Extract("blank.txt", Utf8("   \n\n  ")));
    }

    [Fact]
    public void ValidatesPastedText()
    {
        var document = UploadedDocumentExtractor.ExtractText("Parking rules", "# Rules\nNo parking after 22:00.");

        Assert.Equal("text", document.SourceType);
        Assert.Equal("Rules", document.Sections[0].Title);
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.ExtractText("", "text"));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.ExtractText("t", "  "));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.ExtractText(
            new string('t', KnowledgeUploadLimits.MaxTitleCharacters + 1), "text"));
        Assert.Throws<InvalidDataException>(() => UploadedDocumentExtractor.ExtractText(
            "t", new string('x', KnowledgeUploadLimits.MaxTextCharacters + 1)));
    }

    [Theory]
    [InlineData("Guest notes.pdf", "uploads/Guest notes.pdf")]
    [InlineData("../../etc/passwd", "uploads/passwd")]
    [InlineData("C:\\temp\\evil'name.txt", "uploads/evil_name.txt")]
    [InlineData("", "uploads/untitled")]
    public void BuildsASafeUploadPathFromAnyFileName(string name, string expected)
    {
        var path = KnowledgeUploadService.SourcePathFor(name);

        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("uploads/guide.pdf", true)]
    [InlineData("uploads/sub/guide.pdf", true)]
    [InlineData("docs/knowledge/product.md", false)]
    [InlineData("uploads/../docs/knowledge/product.md", false)]
    [InlineData("uploads/it's.txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAllowsRemovingPathsUnderTheUploadsPrefix(string? path, bool expected)
    {
        Assert.Equal(expected, KnowledgeUploadService.IsUploadPath(path));
    }

    [Fact]
    public void BuildsStableUploadChunksThatReplaceThemselvesOnReupload()
    {
        ExtractedSection[] sections = [new("Intro", "Hello"), new("Long", string.Join("\n\n", Enumerable.Repeat(new string('x', 2500), 4)))];

        var first = KnowledgeCenterChunker.FromSections("uploads", "uploads/a.txt", "txt", "Admin upload", "2026-10-14", "upload-1", sections);
        var second = KnowledgeCenterChunker.FromSections("uploads", "uploads/a.txt", "txt", "Admin upload", "2026-10-15", "upload-2", sections);

        Assert.True(first.Count >= 3);
        Assert.Equal(first.Select(chunk => chunk.Id), second.Select(chunk => chunk.Id));
        Assert.All(first, chunk => Assert.True(chunk.Content.Length <= KnowledgeCenterChunker.MaxChunkCharacters));
        Assert.Equal(first.Count, first.Select(chunk => chunk.Id).Distinct().Count());
    }

    [Fact]
    public async Task ServiceRefusesToIngestOrRemoveWhenUploadsAreDisabledBeforeAnyNetworkCall()
    {
        var chat = new WorkItemChatOptions(null, "c", "e", null, "i", "o", "p", false);
        var service = new KnowledgeUploadService(new KnowledgeUploadOptions(false), chat, new ThrowingCredential());
        var document = UploadedDocumentExtractor.ExtractText("Notes", "Some text");

        await Assert.ThrowsAsync<KnowledgeUploadDisabledException>(() =>
            service.IngestAsync("Notes.txt", document, CancellationToken.None));
        await Assert.ThrowsAsync<KnowledgeUploadDisabledException>(() =>
            service.RemoveAsync("uploads/Notes.txt", CancellationToken.None));
        Assert.False(service.Enabled);
    }

    [Fact]
    public async Task ServiceNeverRemovesRepositoryKnowledge()
    {
        var chat = new WorkItemChatOptions("https://x/", "c", "e", "https://s/", "i", "o", "p", false);
        var service = new KnowledgeUploadService(new KnowledgeUploadOptions(true), chat, new ThrowingCredential());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RemoveAsync("docs/knowledge/product.md", CancellationToken.None));
    }

    [Fact]
    public void PromptMarksUploadedSourcesAsApprovedUploadsNotRepositoryDocs()
    {
        var prompt = WorkItemChatService.BuildSystemPrompt(
            null,
            [new KnowledgeSource("Page 1", "uploads/guide.pdf", "x"), new KnowledgeSource("Rules", "docs/knowledge/domain.md", "y")]);

        Assert.Contains("uploads/guide.pdf, user-uploaded and approved in the admin page", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("domain.md, user-uploaded", prompt, StringComparison.Ordinal);
        Assert.Contains("prefer repository documentation", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void RetrievalExcludesOnlyPendingUploads()
    {
        Assert.Equal($"Owner ne '{KnowledgeApproval.PendingOwner}'", KnowledgeApproval.SearchFilter);
        Assert.NotEqual(KnowledgeApproval.PendingOwner, KnowledgeApproval.ApprovedOwner);
    }

    [Fact]
    public async Task ServiceRefusesToApproveWhenDisabledOrForRepositoryPaths()
    {
        var chat = new WorkItemChatOptions("https://x/", "c", "e", "https://s/", "i", "o", "p", false);
        var disabled = new KnowledgeUploadService(new KnowledgeUploadOptions(false), chat, new ThrowingCredential());
        var enabled = new KnowledgeUploadService(new KnowledgeUploadOptions(true), chat, new ThrowingCredential());

        await Assert.ThrowsAsync<KnowledgeUploadDisabledException>(() =>
            disabled.ApproveAsync("uploads/a.txt", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            enabled.ApproveAsync("docs/knowledge/product.md", CancellationToken.None));
    }

    private sealed class ThrowingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No token must be requested.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No token must be requested.");
    }
}
