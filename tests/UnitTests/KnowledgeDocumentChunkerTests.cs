using AgenticHotelBooking.Infrastructure;

namespace AgenticHotelBooking.UnitTests;

public sealed class KnowledgeDocumentChunkerTests
{
    [Fact]
    public void ProducesStableRevisionedChunksWithRequiredMetadata()
    {
        const string markdown = """
            ---
            owner: Security owner
            last_reviewed: 2026-09-29
            ---
            # Security

            Overview.

            ## Identity

            Use managed identity.
            """;

        var first = KnowledgeDocumentChunker.Chunk("docs\\knowledge\\security.md", markdown, "abc123");
        var second = KnowledgeDocumentChunker.Chunk("docs\\knowledge\\security.md", markdown, "abc123");

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(item => item.Id), second.Select(item => item.Id));
        Assert.All(first, item =>
        {
            Assert.Equal("abc123", item.Revision);
            Assert.Equal("docs/knowledge/security.md", item.Path);
            Assert.Equal("Security owner", item.Owner);
            Assert.Equal("2026-09-29", item.LastReviewed);
        });
    }

    [Fact]
    public void RejectsDocumentsWithoutCanonicalMetadata()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            KnowledgeDocumentChunker.Chunk("docs/knowledge/product.md", "# Product", "abc123"));

        Assert.Contains("YAML metadata", error.Message);
    }
}
