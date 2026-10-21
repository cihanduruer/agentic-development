using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgenticHotelBooking.Infrastructure;

public sealed record KnowledgeEmailOptions(string? Endpoint, string? SenderAddress);

public sealed record KnowledgeEmailChallenge(
    byte[] Hash, string Nonce, DateTimeOffset ExpiresAt, DateTimeOffset ResendAfter, int Attempts, bool Consumed);

public sealed record VersionedKnowledgeChallenge(KnowledgeEmailChallenge Challenge, string Version);

public interface IKnowledgeChallengeStore
{
    Task<VersionedKnowledgeChallenge?> GetAsync(string key, CancellationToken cancellationToken);
    Task<bool> TrySaveAsync(string key, KnowledgeEmailChallenge challenge, string? version, CancellationToken cancellationToken);
}

public interface IKnowledgeEmailSender
{
    Task SendCodeAsync(string email, string code, CancellationToken cancellationToken);
}

public sealed class AzureKnowledgeEmailSender(KnowledgeEmailOptions options, TokenCredential credential) : IKnowledgeEmailSender
{
    public async Task SendCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var client = new EmailClient(new Uri(options.Endpoint
            ?? throw new InvalidOperationException("Knowledge email endpoint is required.")), credential);
        var message = new EmailMessage(options.SenderAddress, email, new EmailContent("Hotel administrator sign-in code")
        {
            PlainText = $"Your Hotel administrator sign-in code is {code}.\n\nUse it to access AI Product Management, Knowledge Center, architecture and operations. It expires in 5 minutes and can be used once. Do not share it. If you did not request this code, ignore this email."
        });
        var operation = await client.SendAsync(WaitUntil.Completed, message, cancellationToken);
        if (operation.Value.Status != EmailSendStatus.Succeeded)
        {
            throw new InvalidOperationException("The email service did not accept the sign-in email.");
        }
    }
}

public sealed class BlobKnowledgeChallengeStore(KnowledgeStorageOptions options, TokenCredential credential) : IKnowledgeChallengeStore
{
    private readonly Lazy<BlobContainerClient> container = new(() => new BlobContainerClient(
        new Uri((options.Endpoint ?? throw new InvalidOperationException("Knowledge storage endpoint is required."))
            .TrimEnd('/') + "/" + options.Container), credential));

    public async Task<VersionedKnowledgeChallenge?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.Value.GetBlobClient($"authentication/{key}.json").DownloadContentAsync(cancellationToken);
            return new VersionedKnowledgeChallenge(
                response.Value.Content.ToObjectFromJson<KnowledgeEmailChallenge>()
                    ?? throw new InvalidDataException("Stored sign-in challenge is invalid."),
                response.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> TrySaveAsync(string key, KnowledgeEmailChallenge challenge, string? version, CancellationToken cancellationToken)
    {
        try
        {
            await container.Value.GetBlobClient($"authentication/{key}.json").UploadAsync(
                BinaryData.FromObjectAsJson(challenge),
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                    Conditions = version is null
                        ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                        : new BlobRequestConditions { IfMatch = new ETag(version) }
                }, cancellationToken);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            return false;
        }
    }
}
