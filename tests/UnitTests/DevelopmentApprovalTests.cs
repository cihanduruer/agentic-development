using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticHotelBooking.Application;
using AgenticHotelBooking.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace AgenticHotelBooking.UnitTests;

public sealed class DevelopmentApprovalTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Repo = "cihanduruer/agentic-development";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingWriteConfigurationNeverCallsGitHub()
    {
        var handler = new Handler();
        using var service = Create(handler, configured: false);
        var state = await service.GetStateAsync(975, 77, default);
        Assert.False(state.CanApprove);
        Assert.Equal("Unavailable", state.Status);
        var error = await Assert.ThrowsAsync<DevelopmentApprovalException>(() => service.ApproveAsync(new(975, 77, Sha), "admin@example.test", default));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("fork")]
    [InlineData("wrong-work-item")]
    [InlineData("ambiguous-issue")]
    [InlineData("later-page-duplicate")]
    [InlineData("issue-scan-bound")]
    [InlineData("review-skipped")]
    [InlineData("other-check-skipped")]
    [InlineData("pending-alternative")]
    [InlineData("latest-check-failed")]
    [InlineData("expired-artifact")]
    [InlineData("wrong-validation-sha")]
    [InlineData("partial-validation")]
    [InlineData("wrong-qa-digest")]
    [InlineData("qa-failed")]
    [InlineData("wrong-source")]
    public async Task UnverifiedEvidenceNeverAllowsApprovalOrMerge(string variant)
    {
        var handler = new Handler { Variant = variant };
        using var service = Create(handler);
        var state = await service.GetStateAsync(975, 77, default);
        Assert.False(state.CanApprove);
        await Assert.ThrowsAsync<DevelopmentApprovalException>(() => service.ApproveAsync(new(975, 77, Sha, Handler.Digest), "admin@example.test", default));
        Assert.Equal(0, handler.Merges);
        Assert.Empty(handler.ReviewBodies);
    }

    [Fact]
    public async Task ExactHeadWithAllTrustedEvidenceCanBeApprovedWithoutClaimingDeployment()
    {
        var handler = new Handler();
        using var service = Create(handler);
        var state = await service.GetStateAsync(975, 77, default);
        Assert.True(state.CanApprove);
        Assert.Equal(Sha, state.HeadSha);
        Assert.Equal(Handler.Digest, state.MetadataDigest);
        var result = await service.ApproveAsync(new(975, 77, Sha, state.MetadataDigest!), "admin@example.test", default);
        Assert.Equal("Publishing", result.Status);
        Assert.Equal(1, handler.Merges);
        var audit = Assert.Single(handler.ReviewBodies);
        Assert.Contains("Administrator-", audit);
        Assert.DoesNotContain("admin@example.test", audit);
        Assert.Contains(Sha, audit);
        Assert.Equal(Sha, handler.MergeExpectedSha);
    }

    [Fact]
    public async Task RepeatedAndConcurrentApprovalsRecoverOneDurableReceiptAndMergeOnce()
    {
        var handler = new Handler();
        using var service = Create(handler);
        var request = new ApproveDevelopmentRequest(975, 77, Sha, Handler.Digest);
        var results = await Task.WhenAll(
            service.ApproveAsync(request, "admin@example.test", default),
            service.ApproveAsync(request, "admin@example.test", default));
        Assert.All(results, result => Assert.Equal("Publishing", result.Status));
        Assert.Equal(1, handler.Merges);
        Assert.Single(handler.ReviewBodies);
        var state = await service.GetStateAsync(975, 77, default);
        Assert.Equal("Publishing", state.Status);
        Assert.False(state.CanApprove);
        Assert.NotNull(state.ApprovalUrl);
    }

    [Fact]
    public async Task HeadOrAcceptanceEvidenceChangedDuringApprovalCannotMerge()
    {
        foreach (var variant in new[] { "head-changes", "metadata-changes" })
        {
            var handler = new Handler { Variant = variant };
            using var service = Create(handler);
            await Assert.ThrowsAsync<DevelopmentApprovalException>(() => service.ApproveAsync(new(975, 77, Sha, Handler.Digest), "admin@example.test", default));
            Assert.Equal(0, handler.Merges);
        }
    }

    [Fact]
    public async Task StaleRequestedHeadIsRejectedBeforeWritingReceipt()
    {
        var handler = new Handler();
        using var service = Create(handler);
        await Assert.ThrowsAsync<DevelopmentApprovalException>(() => service.ApproveAsync(new(975, 77, new string('a', 40), Handler.Digest), "admin@example.test", default));
        Assert.Empty(handler.ReviewBodies);
        Assert.Equal(0, handler.Merges);
    }

    [Fact]
    public async Task LaterPageSoleCanonicalIssueCanBeApproved()
    {
        var handler = new Handler { Variant = "later-page-match" };
        using var service = Create(handler);
        var state = await service.GetStateAsync(975, 77, default);
        Assert.True(state.CanApprove);
        Assert.Equal(2, handler.IssuePages);
        var result = await service.ApproveAsync(new(975, 77, Sha, state.MetadataDigest!), "admin@example.test", default);
        Assert.Equal("Publishing", result.Status);
        Assert.Equal(1, handler.Merges);
    }

    [Fact]
    public async Task AcceptanceChangedSinceReadinessRequiresRefreshEvenWithRegeneratedQa()
    {
        var handler = new Handler();
        using var service = Create(handler);
        var state = await service.GetStateAsync(975, 77, default);
        Assert.True(state.CanApprove);
        handler.MetadataUpdated = true;
        var error = await Assert.ThrowsAsync<DevelopmentApprovalException>(() =>
            service.ApproveAsync(new(975, 77, Sha, state.MetadataDigest!), "admin@example.test", default));
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(handler.ReviewBodies);
        Assert.Equal(0, handler.Merges);

        var refreshed = await service.GetStateAsync(975, 77, default);
        Assert.True(refreshed.CanApprove);
        Assert.NotEqual(state.MetadataDigest, refreshed.MetadataDigest);
        await service.ApproveAsync(new(975, 77, Sha, refreshed.MetadataDigest!), "admin@example.test", default);
        Assert.Equal(1, handler.Merges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    public async Task MissingOrInvalidAcceptanceDigestIsRejectedBeforeGitHub(string? digest)
    {
        var handler = new Handler();
        using var service = Create(handler);
        var error = await Assert.ThrowsAsync<DevelopmentApprovalException>(() =>
            service.ApproveAsync(new(975, 77, Sha, digest!), "admin@example.test", default));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(0, handler.Requests);
    }

    private static DevelopmentApprovalService Create(Handler handler, bool configured = true) =>
        new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(
            configured ? new Dictionary<string, string?> { ["DevelopmentApproval:GitHubToken"] = "fake-write-token" } : []).Build(), new Clock());

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler : HttpMessageHandler
    {
        private const string Title = "[AB#975] Linear delivery";
        private const string Body = "Azure Boards: AB#975";
        public static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Title + "\n" + Body)));
        private string ActiveDigest => MetadataUpdated
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Title + "\n" + Body + "\nUpdated"))) : Digest;
        public string Variant { get; init; } = "";
        public bool MetadataUpdated { get; set; }
        public int Requests { get; private set; }
        public int IssuePages { get; private set; }
        public int Merges { get; private set; }
        public List<string> ReviewBodies { get; } = [];
        public string? MergeExpectedSha { get; private set; }
        private int pullReads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer fake-write-token", request.Headers.Authorization?.ToString());
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/merge", StringComparison.Ordinal))
            {
                using var merge = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                MergeExpectedSha = merge.RootElement.GetProperty("sha").GetString();
                Merges++;
                return Response(new { merged = true, sha = new string('b', 40) });
            }
            if (path.EndsWith("/reviews", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    using var review = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    ReviewBodies.Add(review.RootElement.GetProperty("body").GetString()!);
                    return Response(new { html_url = $"https://github.com/{Repo}/pull/77#pullrequestreview-1" });
                }
                return Response(ReviewBodies.Select(body => new
                {
                    user = new { login = "demo-approver" },
                    state = "APPROVED",
                    commit_id = Sha,
                    body,
                    html_url = $"https://github.com/{Repo}/pull/77#pullrequestreview-1"
                }));
            }
            if (path.EndsWith("/pulls/77", StringComparison.Ordinal))
            {
                pullReads++;
                return Response(new
                {
                    title = Title,
                    body = MetadataUpdated || Variant == "metadata-changes" && pullReads > 1 ? Body + "\nUpdated" : Body,
                    state = "open",
                    draft = Variant == "draft",
                    merged = Merges > 0,
                    html_url = $"https://github.com/{Repo}/pull/77",
                    merge_commit_sha = new string('b', 40),
                    @base = new { @ref = "main", repo = new { full_name = Repo } },
                    head = new
                    {
                        sha = Variant == "head-changes" && pullReads > 1 ? new string('a', 40) : Sha,
                        repo = new { full_name = Variant == "fork" ? "attacker/fork" : Repo }
                    }
                });
            }
            if (path == "/user") return Response(new { login = "demo-approver" });
            if (path.EndsWith("/issues", StringComparison.Ordinal))
            {
                IssuePages++;
                if (Variant == "issue-scan-bound" ||
                    Variant is "later-page-match" or "later-page-duplicate" &&
                    request.RequestUri.Query.EndsWith("&page=1", StringComparison.Ordinal))
                    return Response(Enumerable.Range(0, 100).Select(index => new
                    {
                        title = Variant == "later-page-duplicate" && index == 0 ? Title : "Unrelated issue",
                        body = "",
                        author_association = "OWNER"
                    }));
                return Response(Enumerable.Range(0, Variant == "ambiguous-issue" ? 2 : 1)
                    .Select(_ => new { title = Variant == "wrong-work-item" ? "[AB#976] Other" : Title, body = "", author_association = "OWNER" }));
            }
            if (path.EndsWith("/check-runs", StringComparison.Ordinal))
                return Response(new
                {
                    total_count = 2,
                    check_runs = new[]
                    {
                        new { name = "validate", status = "completed", conclusion = Variant == "latest-check-failed" ? "failure" : "success" },
                        new { name = Variant == "other-check-skipped" ? "Independent QA evidence gate" : "Copilot review not required",
                            status = Variant == "pending-alternative" ? "queued" : "completed", conclusion = "skipped" }
                    }
                });
            if (path.Contains("/workflows/", StringComparison.Ordinal))
            {
                var review = path.Contains("hotel-code-review", StringComparison.Ordinal);
                return Response(new
                {
                    workflow_runs = new[] { Run(review ? 700 : 800,
                    review ? "hotel-code-review.yml" : "hotel-browser-evidence.yml", review ? "pull_request_target" : "pull_request") }
                });
            }
            if (path.EndsWith("/zip", StringComparison.Ordinal))
            {
                var qa = path.Contains("/600/", StringComparison.Ordinal);
                object content = qa ? new
                {
                    schemaVersion = 1,
                    status = Variant == "qa-failed" ? "failed" : "passed",
                    pullRequest = 77,
                    headSha = Sha,
                    metadataDigest = Variant == "wrong-qa-digest" ? new string('0', 64) : ActiveDigest,
                    trustedKnowledgeRevision = Sha,
                    failures = Array.Empty<string>(),
                    completedAtUtc = Now,
                    testRuns = new[] { new { executed = 3, failed = 0, passed = 3, total = 3 } }
                } : new
                {
                    workflow = "pr-validation.yml",
                    @event = "workflow_dispatch",
                    targetSha = Variant == "wrong-validation-sha" ? new string('a', 40) : Sha,
                    checkedOutSha = Sha,
                    runId = "500",
                    sqlBootstrapperCoverage = Variant == "partial-validation" ? "demo-skip" : "full"
                };
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(qa ? "qa-result.json" : "validation.json", content)) };
            }
            if (path.EndsWith("/jobs", StringComparison.Ordinal))
            {
                string[] names = path.Contains("/700/", StringComparison.Ordinal)
                    ? ["Request Copilot review", "Wait for current-head review and evaluate findings"]
                    : path.Contains("/800/", StringComparison.Ordinal)
                        ? ["Run isolated browser evidence", "Run admin console browser smoke", "Verify evidence provenance"]
                        : path.Contains("/600/", StringComparison.Ordinal) ? ["Evaluate QA evidence"]
                        : ["Restore", "Format", "Build", "Test", "Test SQL bootstrapper source mutations",
                            "Test SQL bootstrapper evidence gate", "Test SQL bootstrapper evidence mutants",
                            "Test SQL bootstrapper against SQL Server", "Verify SQL bootstrapper evidence"];
                return Response(new
                {
                    total_count = 1,
                    jobs = new[] { new { status = "completed", conclusion = "success",
                    steps = names.Select(name => new { name, status = "completed",
                        conclusion = Variant == "review-skipped" && name == "Request Copilot review" ? "skipped" : "success" }) } }
                });
            }
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                var qa = path.Contains("/600/", StringComparison.Ordinal) || request.RequestUri.Query.Contains("qa-evidence", StringComparison.Ordinal);
                var id = qa ? 600 : 500;
                return Response(new
                {
                    total_count = 1,
                    artifacts = new[] { new
                {
                    id, name = qa ? $"qa-evidence-{Sha}-{ActiveDigest}" : $"pr-validation-evidence-{Sha}",
                    expired = Variant == "expired-artifact", expires_at = Now.AddDays(1), created_at = Now.AddMinutes(-2),
                    workflow_run = new { id }
                } }
                });
            }
            if (path.EndsWith("/runs/500", StringComparison.Ordinal)) return Response(Run(500, "pr-validation.yml", "workflow_dispatch"));
            if (path.EndsWith("/runs/600", StringComparison.Ordinal)) return Response(Run(600, "qa-evidence.yml", "workflow_run"));
            throw new InvalidOperationException("Unexpected fake GitHub path: " + path);
        }

        private object Run(int id, string file, string trigger) => new
        {
            id,
            path = ".github/workflows/" + file,
            status = "completed",
            conclusion = "success",
            head_sha = Sha,
            head_branch = "main",
            @event = trigger,
            updated_at = Now.AddMinutes(-1),
            repository = new { full_name = Variant == "wrong-source" ? "attacker/fork" : Repo },
            head_repository = new { full_name = Repo }
        };
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        private static byte[] Archive(string name, object value)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
                writer.Write(JsonSerializer.Serialize(value));
            return memory.ToArray();
        }
    }
}
