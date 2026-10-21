using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace AgenticHotelBooking.Infrastructure;

public static class KnowledgeArchive
{
    public static async Task<IReadOnlyList<KnowledgeDocument>> ReadLegacyAsync(
        SearchClient client, CancellationToken cancellationToken)
    {
        var options = new SearchOptions();
        var response = await client.SearchAsync<KnowledgeCenterDocument>("*", options, cancellationToken);
        var blocks = new List<KnowledgeCenterDocument>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            if (blocks.Count >= KnowledgeCenterSearch.MaxListedBlocks)
            {
                throw new InvalidDataException("Archive limit exceeded. No partial archive is permitted.");
            }
            blocks.Add(result.Document);
        }
        return blocks.GroupBy(block => block.SourcePath).Select(group =>
        {
            if (group.Select(block => block.Revision).Distinct(StringComparer.Ordinal).Count() != 1 ||
                group.GroupBy(block => block.ChunkIndex).Any(chunk => chunk.Count() != 1))
            {
                throw new InvalidDataException($"Conflicting indexed versions for {group.Key}; resolve before migration.");
            }
            var first = group.First();
            return new KnowledgeDocument(first.SourcePath, first.SourceType, first.Revision,
                group.Any(block => block.Owner == KnowledgeApproval.PendingOwner) ? KnowledgeApproval.PendingOwner : first.Owner,
                first.LastReviewed,
                group.OrderBy(block => block.ChunkIndex).Select(block => new ExtractedSection(block.Title, block.Content)).ToArray());
        }).OrderBy(document => document.Path, StringComparer.Ordinal).ToArray();
    }
}
