using AgenticHotelBooking.Infrastructure;

namespace AgenticHotelBooking.UnitTests;

public sealed class KnowledgeCenterChunkerTests
{
    private const string Markdown = """
        ---
        owner: Product owner
        last_reviewed: 2026-10-13
        ---
        # Product

        ## Objective
        Guests book rooms.

        ## Rules
        Rooms cannot be double booked.
        """;

    [Fact]
    public void ProducesStableAreaScopedChunksWithSourceMetadata()
    {
        var first = KnowledgeCenterChunker.FromMarkdown("hotel-site", "docs/knowledge/product.md", Markdown, "abc123");
        var second = KnowledgeCenterChunker.FromMarkdown("hotel-site", "docs\\knowledge\\product.md", Markdown, "def456");

        Assert.Equal(3, first.Count);
        Assert.All(first, chunk =>
        {
            Assert.Equal("hotel-site", chunk.Area);
            Assert.Equal("markdown", chunk.SourceType);
            Assert.Equal("docs/knowledge/product.md", chunk.SourcePath);
            Assert.Equal("Product owner", chunk.Owner);
        });
        Assert.Equal(first.Select(chunk => chunk.Id), second.Select(chunk => chunk.Id));
        Assert.Equal(first.Count, first.Select(chunk => chunk.Id).Distinct().Count());
        Assert.Equal([0, 1, 2], first.Select(chunk => chunk.ChunkIndex));
    }

    [Fact]
    public void KeepsDifferentAreasApartForTheSamePath()
    {
        var hotel = KnowledgeCenterChunker.FromMarkdown("hotel-site", "docs/a.md", Markdown, "r1");
        var other = KnowledgeCenterChunker.FromMarkdown("platform", "docs/a.md", Markdown, "r1");

        Assert.Empty(hotel.Select(chunk => chunk.Id).Intersect(other.Select(chunk => chunk.Id)));
    }

    [Fact]
    public void SplitsOversizedSectionsOnParagraphBoundariesWithoutLosingText()
    {
        var paragraph = new string('x', 2500);
        var content = string.Join("\n\n", Enumerable.Repeat(paragraph, 5));

        var parts = KnowledgeCenterChunker.SplitLongContent(content).ToArray();

        Assert.True(parts.Length > 1);
        Assert.All(parts, part => Assert.True(part.Length <= KnowledgeCenterChunker.MaxChunkCharacters));
        Assert.Equal(5 * paragraph.Length, parts.Sum(part => part.Count(character => character == 'x')));
    }

    [Fact]
    public void IndexSchemaDefinesTheVectorFieldAndSemanticConfiguration()
    {
        var index = KnowledgeCenterSchema.BuildIndex("hotel-knowledge-center");
        var vector = Assert.Single(index.Fields, field => field.Name == nameof(KnowledgeCenterDocument.ContentVector));

        Assert.Equal(KnowledgeCenterSchema.EmbeddingDimensions, vector.VectorSearchDimensions);
        Assert.Equal(KnowledgeCenterSchema.VectorProfileName, vector.VectorSearchProfileName);
        Assert.Contains(index.SemanticSearch.Configurations, configuration =>
            configuration.Name == KnowledgeCenterSchema.SemanticConfigurationName);
    }
}
