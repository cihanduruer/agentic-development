using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace AgenticHotelBooking.Infrastructure;

public sealed record KnowledgeStorageOptions(string? Endpoint, string Container = "knowledge-documents");

public sealed record KnowledgeDocument(
    string Path,
    string SourceType,
    string Revision,
    string Owner,
    string LastReviewed,
    IReadOnlyList<ExtractedSection> Sections,
    byte[]? Original = null,
    bool Deleted = false)
{
    public bool Approved => Owner != KnowledgeApproval.PendingOwner && !Deleted;
}

public interface IKnowledgeWriteLease : IAsyncDisposable
{
    CancellationToken Token { get; }
}

public interface IKnowledgeDocumentStore
{
    Task<IReadOnlyList<KnowledgeDocument>> ListAsync(CancellationToken cancellationToken);
    Task<KnowledgeDocument?> GetAsync(string path, CancellationToken cancellationToken);
    Task SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken);
    Task DeleteAsync(string path, CancellationToken cancellationToken);
    Task<IKnowledgeWriteLease> LockAsync(CancellationToken cancellationToken);
}

public sealed class BlobKnowledgeDocumentStore(KnowledgeStorageOptions options, TokenCredential credential)
    : IKnowledgeDocumentStore
{
    private readonly Lazy<BlobContainerClient> container = new(() => new BlobContainerClient(
        new Uri((options.Endpoint ?? throw new InvalidOperationException("KnowledgeStorage:Endpoint is required."))
            .TrimEnd('/') + "/" + options.Container), credential));

    private static string Key(string path) =>
        "documents/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant() + ".json";

    public async Task<IReadOnlyList<KnowledgeDocument>> ListAsync(CancellationToken cancellationToken)
    {
        var documents = new List<KnowledgeDocument>();
        await foreach (var blob in container.Value.GetBlobsAsync(BlobTraits.None, BlobStates.None, "documents/", cancellationToken))
        {
            if (documents.Count >= KnowledgeCenterSearch.MaxListedBlocks)
            {
                throw new InvalidOperationException("The document inventory limit has been exceeded.");
            }

            var result = await container.Value.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
            documents.Add(result.Value.Content.ToObjectFromJson<KnowledgeDocument>()
                ?? throw new InvalidDataException("A stored knowledge document is invalid."));
        }

        return documents.OrderBy(document => document.Path, StringComparer.Ordinal).ToArray();
    }

    public async Task<KnowledgeDocument?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await container.Value.GetBlobClient(Key(path)).DownloadContentAsync(cancellationToken);
            return result.Value.Content.ToObjectFromJson<KnowledgeDocument>()
                ?? throw new InvalidDataException("A stored knowledge document is invalid.");
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken) =>
        await container.Value.GetBlobClient(Key(document.Path)).UploadAsync(
            BinaryData.FromObjectAsJson(document),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } },
            cancellationToken);

    public async Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        await container.Value.GetBlobClient(Key(path)).DeleteIfExistsAsync(
            DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);

    public async Task<IKnowledgeWriteLease> LockAsync(CancellationToken cancellationToken)
    {
        var blob = container.Value.GetBlobClient("management.lock");
        try
        {
            await blob.UploadAsync(BinaryData.FromString("Knowledge Center write lock"), cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.ErrorCode == "BlobAlreadyExists")
        {
            // The lock blob is deliberately shared by all API instances and migration tools.
        }

        try
        {
            var lease = blob.GetBlobLeaseClient();
            await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
            return new WriteLease(lease, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            throw new InvalidOperationException("Another knowledge update is in progress. Try again shortly.", exception);
        }
    }

    private sealed class WriteLease : IKnowledgeWriteLease
    {
        private readonly BlobLeaseClient client;
        private readonly CancellationTokenSource lifetime;
        private readonly Task renewal;

        public WriteLease(BlobLeaseClient client, CancellationToken cancellationToken)
        {
            this.client = client;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            renewal = RenewAsync();
        }

        public CancellationToken Token => lifetime.Token;

        private async Task RenewAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync(lifetime.Token))
                {
                    await client.RenewAsync(cancellationToken: lifetime.Token);
                }
            }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            try
            {
                await renewal;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Normal shutdown of the renewal loop.
            }
            finally
            {
                await client.ReleaseAsync(cancellationToken: CancellationToken.None);
                lifetime.Dispose();
            }
        }
    }
}
