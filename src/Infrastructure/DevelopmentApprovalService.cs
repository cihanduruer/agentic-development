using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticHotelBooking.Application;
using Microsoft.Extensions.Configuration;

namespace AgenticHotelBooking.Infrastructure;

public sealed class DevelopmentApprovalService(HttpClient github, IConfiguration configuration, TimeProvider time)
    : IDevelopmentApprovalService, IDisposable
{
    private const string Repository = "cihanduruer/agentic-development";
    private const string Api = "https://api.github.com/";
    private const string Prefix = "Hotel development approval v1\n";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim approvalLock = new(1, 1);
    private string? Token => configuration["DevelopmentApproval:GitHubToken"];

    public async Task<DevelopmentApprovalState> GetStateAsync(int workItemId, int pullRequestNumber, CancellationToken cancellationToken)
    {
        if (workItemId <= 0 || pullRequestNumber <= 0)
            throw new DevelopmentApprovalException(400, "A valid request and pull request are required.");
        if (string.IsNullOrWhiteSpace(Token))
            return new(pullRequestNumber, "", false, "Unavailable", "Demo approval is not configured. Contact the administrator.");
        RequireUserCredential();
        try
        {
            var context = await ReadContextAsync(workItemId, pullRequestNumber, cancellationToken);
            var receipt = await FindReceiptAsync(context, cancellationToken);
            if (context.Merged)
                return new(pullRequestNumber, context.Sha, false, receipt is null ? "Unverified" : "Publishing",
                    receipt is null ? "This change was merged outside this page; its approval record is unavailable." :
                    "Approved. Follow Publishing and Ready for the actual deployment result.",
                    receipt?.Url, receipt?.Actor, receipt?.At);
            await VerifyEvidenceAsync(context, cancellationToken);
            return new(pullRequestNumber, context.Sha, true, "Ready",
                "Your change has passed its checks. Approve it when you are ready to publish to the demo site.",
                receipt?.Url, receipt?.Actor, receipt?.At, context.Digest);
        }
        catch (DevelopmentApprovalException exception) when (exception.StatusCode == 409)
        {
            return new(pullRequestNumber, "", false, "Waiting", exception.Message);
        }
    }

    public async Task<DevelopmentApprovalResponse> ApproveAsync(
        ApproveDevelopmentRequest request, string administrator, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Token))
            throw new DevelopmentApprovalException(503, "Demo approval is not configured.");
        RequireUserCredential();
        if (string.IsNullOrWhiteSpace(administrator) || !IsSha(request.ExpectedHeadSha) ||
            request.ExpectedMetadataDigest is not { Length: 64 } ||
            !Regex.IsMatch(request.ExpectedMetadataDigest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new DevelopmentApprovalException(400, "A validated administrator, exact revision and acceptance-metadata digest are required.");
        await approvalLock.WaitAsync(cancellationToken);
        try
        {
            var context = await ReadContextAsync(request.WorkItemId, request.PullRequestNumber, cancellationToken);
            Require(context.Sha == request.ExpectedHeadSha, "The change has been updated. Refresh and review it before approving.");
            Require(context.Digest == request.ExpectedMetadataDigest,
                "The acceptance evidence has changed. Refresh and review it before approving.");
            var receipt = await FindReceiptAsync(context, cancellationToken);
            if (context.Merged)
            {
                Require(receipt is not null, "This change was merged without an approval record from this page.");
                return Published(context, receipt!);
            }
            await VerifyEvidenceAsync(context, cancellationToken);
            var actor = "Administrator-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(administrator)))[..16];
            if (receipt is null)
            {
                var audit = new Audit(request.WorkItemId, request.PullRequestNumber, context.Sha, context.Digest, actor, time.GetUtcNow());
                var review = await SendJsonAsync(HttpMethod.Post, $"repos/{Repository}/pulls/{request.PullRequestNumber}/reviews",
                    new { commit_id = context.Sha, @event = "APPROVE", body = Prefix + JsonSerializer.Serialize(audit, Json) }, cancellationToken);
                receipt = new Receipt(Text(review, "html_url"), actor, audit.At);
            }
            var fresh = await ReadContextAsync(request.WorkItemId, request.PullRequestNumber, cancellationToken);
            Require(fresh.Sha == context.Sha && fresh.Digest == context.Digest,
                "The change or its acceptance evidence changed during approval. Refresh and review it again.");
            if (fresh.Merged) return Published(fresh, receipt);
            await VerifyEvidenceAsync(fresh, cancellationToken);
            var final = await ReadContextAsync(request.WorkItemId, request.PullRequestNumber, cancellationToken);
            Require(final.Sha == context.Sha && final.Digest == context.Digest,
                "The change or its acceptance evidence changed during final verification. Refresh and review it again.");
            if (final.Merged) return Published(final, receipt);
            var merge = await SendJsonAsync(HttpMethod.Put, $"repos/{Repository}/pulls/{request.PullRequestNumber}/merge",
                new { sha = context.Sha, merge_method = "squash" }, cancellationToken);
            Require(merge.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
                "GitHub did not merge the change. Refresh to check the required checks and merge status.");
            return new("Publishing", "Approved. Publishing will continue automatically; the demo is not ready until its final checks finish.",
                context.Url, receipt.Url, Text(merge, "sha"));
        }
        finally { approvalLock.Release(); }
    }

    private async Task<Context> ReadContextAsync(int workItemId, int number, CancellationToken cancellationToken)
    {
        if (workItemId <= 0 || number <= 0)
            throw new DevelopmentApprovalException(400, "A valid request and pull request are required.");
        var pull = await GetAsync($"repos/{Repository}/pulls/{number}", cancellationToken);
        var merged = pull.TryGetProperty("merged", out var value) && value.ValueKind == JsonValueKind.True;
        Require(Text(pull.GetProperty("base"), "ref") == "main" &&
            Text(pull.GetProperty("base").GetProperty("repo"), "full_name") == Repository &&
            Text(pull.GetProperty("head").GetProperty("repo"), "full_name") == Repository &&
            (merged || Text(pull, "state") == "open"), "Only this repository's open changes to main can be approved.");
        Require(!pull.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.True,
            "Development is still being prepared. Approval becomes available when the change is ready for review.");
        var title = Text(pull, "title");
        var body = Text(pull, "body");
        var ids = Regex.Matches(title + "\n" + body, @"\bAB#([1-9][0-9]*)\b", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        Require(ids.Length == 1 && ids[0] == workItemId.ToString(CultureInfo.InvariantCulture),
            "The change does not identify this request unambiguously.");
        List<JsonElement> canonical = [];
        var complete = false;
        for (var page = 1; page <= 10; page++)
        {
            var issues = await GetAsync($"repos/{Repository}/issues?state=all&per_page=100&sort=updated&direction=desc&page={page}", cancellationToken);
            canonical.AddRange(issues.EnumerateArray().Where(issue =>
                !issue.TryGetProperty("pull_request", out _) &&
                Text(issue, "author_association") is "OWNER" or "MEMBER" or "COLLABORATOR" &&
                (Regex.IsMatch(Text(issue, "title"), $@"^\[AB#{workItemId}\](?:\s|$)", RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(Text(issue, "body"),
                        $@"(?m)^\s*Azure Boards work item:\s*\[AB#{workItemId}\]\(https://dev\.azure\.com/ai-enabled-ado-org/sample-project/_workitems/edit/{workItemId}\)\s*$",
                        RegexOptions.CultureInvariant))));
            if (issues.GetArrayLength() < 100)
            {
                complete = true;
                break;
            }
        }
        Require(complete, "The request listing could not be completed safely. Contact the administrator.");
        Require(canonical.Count == 1, "The linked request could not be verified uniquely. Refresh or contact the administrator.");
        var sha = Text(pull.GetProperty("head"), "sha");
        Require(IsSha(sha), "The current change revision could not be verified.");
        return new(workItemId, number, sha, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + body))),
            Text(pull, "html_url"), merged, Text(pull, "merge_commit_sha"));
    }

    private async Task VerifyEvidenceAsync(Context context, CancellationToken cancellationToken)
    {
        var checks = await GetAsync($"repos/{Repository}/commits/{context.Sha}/check-runs?filter=latest&per_page=100", cancellationToken);
        Require(checks.GetProperty("total_count").GetInt32() is > 0 and <= 100 &&
            checks.GetProperty("check_runs").EnumerateArray().All(check =>
                Text(check, "status") == "completed" &&
                (Text(check, "conclusion") == "success" ||
                    Text(check, "name") == "Copilot review not required" && Text(check, "conclusion") == "skipped")),
            "Current revision checks are still running, failed, or incomplete.");
        var reviews = await GetAsync($"repos/{Repository}/actions/workflows/hotel-code-review.yml/runs?per_page=30&head_sha={context.Sha}", cancellationToken);
        var run = reviews.GetProperty("workflow_runs").EnumerateArray()
            .Where(candidate => TrustedRun(candidate, ".github/workflows/hotel-code-review.yml") &&
                Text(candidate, "event") == "pull_request_target" && Text(candidate, "head_sha") == context.Sha)
            .OrderByDescending(candidate => candidate.GetProperty("id").GetInt64()).FirstOrDefault();
        Require(run.ValueKind == JsonValueKind.Object, "Waiting for successful code review of this exact revision.");
        await RequireStepsAsync(run, ["Request Copilot review", "Wait for current-head review and evaluate findings"], cancellationToken);

        var browser = await GetAsync($"repos/{Repository}/actions/workflows/hotel-browser-evidence.yml/runs?per_page=30&head_sha={context.Sha}", cancellationToken);
        var browserRun = browser.GetProperty("workflow_runs").EnumerateArray()
            .Where(candidate => TrustedRun(candidate, ".github/workflows/hotel-browser-evidence.yml") &&
                Text(candidate, "event") == "pull_request" && Text(candidate, "head_sha") == context.Sha)
            .OrderByDescending(candidate => candidate.GetProperty("id").GetInt64()).FirstOrDefault();
        Require(browserRun.ValueKind == JsonValueKind.Object, "Waiting for browser checks of this exact revision.");
        await RequireStepsAsync(browserRun, ["Run isolated browser evidence", "Run admin console browser smoke", "Verify evidence provenance"], cancellationToken);

        var validation = await ReadArtifactAsync($"pr-validation-evidence-{context.Sha}", ".github/workflows/pr-validation.yml",
            "validation.json", context, false, cancellationToken);
        Require(Text(validation, "workflow") == "pr-validation.yml" && Text(validation, "event") == "workflow_dispatch" &&
            Text(validation, "targetSha") == context.Sha && Text(validation, "checkedOutSha") == context.Sha,
            "Trusted validation does not match this exact revision.");
        Require(!validation.TryGetProperty("sqlBootstrapperCoverage", out var coverage) || coverage.GetString() == "full",
            "Demo-only partial validation cannot authorize publishing.");
        var qa = await ReadArtifactAsync($"qa-evidence-{context.Sha}-{context.Digest}", ".github/workflows/qa-evidence.yml",
            "qa-result.json", context, true, cancellationToken);
        Require(qa.GetProperty("schemaVersion").GetInt32() == 1 && Text(qa, "status") == "passed" &&
            qa.GetProperty("pullRequest").GetInt32() == context.Number && Text(qa, "headSha") == context.Sha &&
            Text(qa, "metadataDigest") == context.Digest && IsSha(Text(qa, "trustedKnowledgeRevision")) &&
            qa.GetProperty("failures").GetArrayLength() == 0 &&
            qa.GetProperty("testRuns").GetArrayLength() > 0 &&
            qa.GetProperty("testRuns").EnumerateArray().All(test =>
                test.GetProperty("executed").GetInt32() > 0 && test.GetProperty("failed").GetInt32() == 0 &&
                test.GetProperty("passed").GetInt32() == test.GetProperty("total").GetInt32()) &&
            Recent(Text(qa, "completedAtUtc")), "Independent QA evidence is incomplete, outdated, or belongs to different acceptance evidence.");
    }

    private async Task<JsonElement> ReadArtifactAsync(string name, string workflow, string file, Context context, bool qa, CancellationToken cancellationToken)
    {
        var listing = await GetAsync($"repos/{Repository}/actions/artifacts?name={name}&per_page=30", cancellationToken);
        var candidates = listing.GetProperty("artifacts").EnumerateArray().ToArray();
        Require(candidates.Length < 30, "Too many evidence artifacts exist to verify safely.");
        foreach (var artifact in candidates.OrderByDescending(item => item.GetProperty("id").GetInt64()))
        {
            if (Text(artifact, "name") != name || artifact.GetProperty("expired").GetBoolean() ||
                !DateTimeOffset.TryParse(Text(artifact, "expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) ||
                expires <= time.GetUtcNow() || !Recent(Text(artifact, "created_at"))) continue;
            var runId = artifact.GetProperty("workflow_run").GetProperty("id").GetInt64();
            var run = await GetAsync($"repos/{Repository}/actions/runs/{runId}", cancellationToken);
            if (!TrustedRun(run, workflow) ||
                (qa ? Text(run, "event") is not ("workflow_run" or "workflow_dispatch") :
                    Text(run, "event") != "workflow_dispatch") || Text(run, "head_branch") != "main") continue;
            var attached = await GetAsync($"repos/{Repository}/actions/runs/{runId}/artifacts?per_page=100", cancellationToken);
            Require(attached.GetProperty("total_count").GetInt32() <= 100, "Evidence attachment listing is incomplete.");
            if (!attached.GetProperty("artifacts").EnumerateArray().Any(item =>
                item.GetProperty("id").GetInt64() == artifact.GetProperty("id").GetInt64() && Text(item, "name") == name &&
                !item.GetProperty("expired").GetBoolean())) continue;
            await RequireStepsAsync(run, qa ? ["Evaluate QA evidence"] :
                ["Restore", "Format", "Build", "Test", "Test SQL bootstrapper source mutations",
                    "Test SQL bootstrapper evidence gate", "Test SQL bootstrapper evidence mutants",
                    "Test SQL bootstrapper against SQL Server", "Verify SQL bootstrapper evidence"], cancellationToken);
            var bytes = await DownloadAsync($"repos/{Repository}/actions/artifacts/{artifact.GetProperty("id").GetInt64()}/zip", cancellationToken);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            Require(zip.Entries.Count <= 100 && zip.Entries.Sum(entry => entry.Length) <= 16 * 1024 * 1024,
                "Evidence archive exceeds the safety limit.");
            var entries = zip.Entries.Where(entry => entry.Name == file).ToArray();
            Require(entries.Length == 1 && entries[0].Length <= MaximumBytes, "Evidence document is missing or ambiguous.");
            await using var stream = entries[0].Open();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var result = document.RootElement.Clone();
            if (!qa) Require(Text(result, "runId") == runId.ToString(CultureInfo.InvariantCulture), "Validation provenance belongs to a different run.");
            return result;
        }
        throw new DevelopmentApprovalException(409, qa ?
            "Waiting for trusted independent QA of this revision and acceptance evidence." : "Waiting for full trusted main-branch validation of this exact revision.");
    }

    private async Task RequireStepsAsync(JsonElement run, string[] required, CancellationToken cancellationToken)
    {
        var jobs = await GetAsync($"repos/{Repository}/actions/runs/{run.GetProperty("id").GetInt64()}/jobs?per_page=100", cancellationToken);
        Require(jobs.GetProperty("total_count").GetInt32() <= 100, "The complete check listing could not be verified.");
        var steps = jobs.GetProperty("jobs").EnumerateArray()
            .Where(job => Text(job, "status") == "completed" && Text(job, "conclusion") == "success")
            .SelectMany(job => job.GetProperty("steps").EnumerateArray()).ToArray();
        Require(required.All(name => steps.Any(step => Text(step, "name") == name &&
            Text(step, "status") == "completed" && Text(step, "conclusion") == "success")),
            "Required checks are missing, skipped, or incomplete. Publishing remains paused.");
    }

    private async Task<Receipt?> FindReceiptAsync(Context context, CancellationToken cancellationToken)
    {
        var user = await GetAsync("user", cancellationToken);
        var reviews = await GetAsync($"repos/{Repository}/pulls/{context.Number}/reviews?per_page=100", cancellationToken);
        Require(reviews.GetArrayLength() < 100, "The complete approval history could not be verified.");
        foreach (var review in reviews.EnumerateArray().Reverse())
        {
            if (Text(review.GetProperty("user"), "login") != Text(user, "login") ||
                Text(review, "state") != "APPROVED" || Text(review, "commit_id") != context.Sha ||
                !Text(review, "body").StartsWith(Prefix, StringComparison.Ordinal)) continue;
            Audit? audit;
            try { audit = JsonSerializer.Deserialize<Audit>(Text(review, "body")[Prefix.Length..], Json); }
            catch (JsonException) { continue; }
            if (audit is not null && audit.WorkItemId == context.WorkItemId && audit.PullRequestNumber == context.Number &&
                audit.HeadSha == context.Sha && audit.MetadataDigest == context.Digest &&
                audit.At > DateTimeOffset.UnixEpoch && audit.At <= time.GetUtcNow().AddMinutes(5) &&
                audit.Actor is not null && Regex.IsMatch(audit.Actor, "^Administrator-[0-9a-f]{16}$", RegexOptions.CultureInvariant))
                return new(Text(review, "html_url"), audit.Actor, audit.At);
        }
        return null;
    }

    private bool TrustedRun(JsonElement run, string path) =>
        Text(run, "path") == path && Text(run, "status") == "completed" && Text(run, "conclusion") == "success" &&
        Text(run.GetProperty("repository"), "full_name") == Repository &&
        Text(run.GetProperty("head_repository"), "full_name") == Repository && Recent(Text(run, "updated_at"));

    private bool Recent(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) &&
        at <= time.GetUtcNow().AddMinutes(5) && at >= time.GetUtcNow().AddHours(-24);
    private static bool IsSha(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private void RequireUserCredential()
    {
        if (Token?.StartsWith("ghs_", StringComparison.Ordinal) == true)
            throw new DevelopmentApprovalException(503,
                "Demo approval requires a user access token. GitHub App installation tokens are not supported.");
    }
    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new DevelopmentApprovalException(409, message);
    }
    private static DevelopmentApprovalResponse Published(Context context, Receipt receipt) =>
        new("Publishing", "Approval is recorded. Follow the publishing and final verification steps for the actual demo status.",
            context.Url, receipt.Url, context.MergeSha);

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        await SendJsonAsync(HttpMethod.Get, path, null, cancellationToken);
    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new DevelopmentApprovalException(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity ? 409 : 502,
                $"GitHub could not complete the approval check (HTTP {(int)response.StatusCode}). No deployment success is assumed.");
        var bytes = await ReadBoundedAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(Api), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.UserAgent.ParseAdd("HotelDevelopmentApproval/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
    private async Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Found && response.Headers.Location is { IsAbsoluteUri: true } location &&
            location.Scheme == Uri.UriSchemeHttps && location.UserInfo.Length == 0 && location.Port == 443 &&
            (location.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
                location.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            using var download = new HttpRequestMessage(HttpMethod.Get, location);
            using var redirected = await github.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!redirected.IsSuccessStatusCode) throw new DevelopmentApprovalException(502, "The trusted evidence archive could not be downloaded.");
            return await ReadBoundedAsync(redirected, cancellationToken);
        }
        if (!response.IsSuccessStatusCode) throw new DevelopmentApprovalException(502, "The evidence archive download was rejected.");
        return await ReadBoundedAsync(response, cancellationToken);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new DevelopmentApprovalException(502, "The evidence response exceeds the safety limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (target.Length + read > MaximumBytes) throw new DevelopmentApprovalException(502, "The evidence response exceeds the safety limit.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }
    public void Dispose() { approvalLock.Dispose(); github.Dispose(); }
    private sealed record Context(int WorkItemId, int Number, string Sha, string Digest, string Url, bool Merged, string MergeSha);
    private sealed record Audit(int WorkItemId, int PullRequestNumber, string HeadSha, string MetadataDigest, string Actor, DateTimeOffset At);
    private sealed record Receipt(string Url, string Actor, DateTimeOffset At);
}
