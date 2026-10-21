using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticHotelBooking.Application;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace AgenticHotelBooking.Infrastructure;

public sealed class DeliveryStatusService(
    HttpClient githubClient,
    HttpClient boardsClient,
    IConfiguration configuration,
    TokenCredential credential,
    TimeProvider timeProvider) : IDeliveryStatusService, IDisposable
{
    private static readonly TimeSpan AuthenticatedCacheLifetime = TimeSpan.FromSeconds(45);
    // Four GitHub requests per refresh, refreshed at most every 5 minutes anonymously (48/hour), stay under the 60 requests/hour unauthenticated quota.
    private static readonly TimeSpan AnonymousCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const string RepositoryFullName = "cihanduruer/agentic-development";
    private const string BoardsOrganization = "ai-enabled-ado-org";
    private const string BoardsProject = "sample-project";
    private static readonly string[] TrustedAuthorAssociations = ["OWNER", "MEMBER", "COLLABORATOR"];
    private const int MaximumUpstreamResponseBytes = 4_194_304;
    private static readonly Regex WorkItemPattern = new(
        @"\bAB#([1-9][0-9]{0,9})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] WorkflowNames =
    [
        "Agentic intake",
        "Copilot cloud agent",
        "PR validation",
        "Hotel code review",
        "Hotel browser evidence",
        "QA evidence",
        "Deploy development",
        "Sync development lifecycle",
        "Release proposal"
    ];
    private static readonly string[] StageNames =
    [
        "Boards intake",
        "GitHub issue and cloud agent",
        "Pull request",
        "PR validation",
        "Copilot review gate",
        "QA evidence",
        "Merge",
        "Development deployment",
        "Lifecycle sync and delivery evidence",
        "Release proposal",
        "Human production approval"
    ];
    private static readonly string[] QaWorkflowNames = ["Hotel browser evidence", "QA evidence"];

    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly Dictionary<int, DateTimeOffset> boardsAttempts = [];
    private readonly Dictionary<int, DateTimeOffset> boardsLastSuccessAt = [];
    private readonly Dictionary<int, string> boardsFailureReasons = [];
    private readonly Dictionary<string, GitHubResponseCache> githubResponseCache = new(StringComparer.Ordinal);
    private DeliveryStatusSnapshot? lastGood;
    private DeliveryStatusSnapshot? noDataFailure;
    private DateTimeOffset lastFetchedAt;
    private DateTimeOffset lastSuccessAt;
    private DateTimeOffset noDataFailureAt;

    private TimeSpan CacheLifetime =>
        string.IsNullOrWhiteSpace(configuration["GitHubStatus:Token"]) ? AnonymousCacheLifetime : AuthenticatedCacheLifetime;

    public async Task<DeliveryStatusSnapshot> GetStatusAsync(
        int? workItemId,
        CancellationToken cancellationToken)
    {
        if (workItemId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workItemId));
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (lastGood is not null && timeProvider.GetUtcNow() - lastFetchedAt < CacheLifetime)
            {
                var refreshed = await LoadSelectedBoardsItemAsync(lastGood, workItemId, cancellationToken);
                lastGood = refreshed with { SelectedDelivery = null };
                return Select(lastGood, workItemId);
            }

            if (lastGood is null && noDataFailure is not null &&
                timeProvider.GetUtcNow() - noDataFailureAt < CacheLifetime)
            {
                return noDataFailure;
            }

            try
            {
                var snapshot = await FetchSnapshotAsync(cancellationToken);
                PruneBoardsCache(snapshot.Deliveries);
                if (lastGood is not null)
                {
                    var oldItems = lastGood.Deliveries
                        .Where(item => item.WorkItemId.HasValue)
                        .ToDictionary(item => item.WorkItemId!.Value);
                    snapshot = snapshot with
                    {
                        Deliveries = snapshot.Deliveries.Select(item =>
                            item with
                            {
                                BoardsWorkItem = item.WorkItemId is int id && oldItems.TryGetValue(id, out var prior)
                                    ? prior.BoardsWorkItem
                                    : null
                            }).ToArray()
                    };
                }

                snapshot = await LoadSelectedBoardsItemAsync(snapshot, workItemId, cancellationToken);
                lastGood = snapshot with { SelectedDelivery = null };
                lastFetchedAt = timeProvider.GetUtcNow();
                lastSuccessAt = lastFetchedAt;
                noDataFailure = null;
                return Select(lastGood, workItemId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsUpstreamFailure(exception))
            {
                if (lastGood is not null)
                {
                    lastGood = lastGood with
                    {
                        IsStale = true,
                        UnreachableMessage = $"Delivery sources unavailable; showing last successful update at {lastSuccessAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC."
                    };
                    lastFetchedAt = timeProvider.GetUtcNow();
                    return Select(lastGood, workItemId);
                }

                noDataFailureAt = timeProvider.GetUtcNow();
                noDataFailure = new DeliveryStatusSnapshot(
                    noDataFailureAt,
                    true,
                    "Delivery sources are unreachable; no previous status is available.",
                    BoardsIsConfigured() ? "Not connected (reason: unavailable)" : BoardsNotConnectedReason(),
                    []);
                return noDataFailure;
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<DeliveryStatusSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken)
    {
        var github = await FetchGitHubAsync(cancellationToken);
        var boardsConfigured = BoardsIsConfigured();
        var boardsConnection = boardsConfigured ? "Unverified" : BoardsNotConnectedReason();
        var deliveries = new List<DeliverySummary>(github.Issues.Count);

        foreach (var issue in github.Issues)
        {
            var pullRequest = issue.WorkItemId is int workItemId
                ? issue.PullRequests.FirstOrDefault(pr => pr.WorkItemId == workItemId)
                : null;
            var stages = BuildStages(issue, pullRequest, github.Runs);
            deliveries.Add(new DeliverySummary(
                issue.WorkItemId,
                issue.Title,
                issue.Url,
                issue.Number,
                issue.UpdatedAt,
                OverallStatus(stages),
                pullRequest?.Title,
                pullRequest?.Number,
                pullRequest?.Url,
                stages,
                null)
            { IsDraft = pullRequest?.IsDraft == true });
        }

        var now = timeProvider.GetUtcNow();
        return new DeliveryStatusSnapshot(now, false, null, boardsConnection, deliveries);
    }

    private async Task<DeliveryStatusSnapshot> LoadSelectedBoardsItemAsync(
        DeliveryStatusSnapshot snapshot,
        int? workItemId,
        CancellationToken cancellationToken)
    {
        if (workItemId is null)
        {
            return snapshot with { SelectedDelivery = null };
        }

        var selected = snapshot.Deliveries.FirstOrDefault(item => item.WorkItemId == workItemId)
            ?? throw new KeyNotFoundException("The work item is not present in recent GitHub deliveries.");
        if (!BoardsIsConfigured())
        {
            return snapshot with { SelectedDelivery = selected };
        }

        var now = timeProvider.GetUtcNow();
        var hasRecentAttempt = boardsAttempts.TryGetValue(workItemId.Value, out var attemptedAt) &&
            now - attemptedAt < CacheLifetime;
        if (hasRecentAttempt && boardsFailureReasons.TryGetValue(workItemId.Value, out var cachedReason))
        {
            return WithBoardsFailure(snapshot, selected, cachedReason);
        }

        if (hasRecentAttempt)
        {
            return snapshot with { BoardsConnection = "Connected", SelectedDelivery = selected };
        }

        try
        {
            var boardItem = await FetchBoardsItemAsync(workItemId.Value, cancellationToken);
            boardsAttempts[workItemId.Value] = timeProvider.GetUtcNow();
            boardsLastSuccessAt[workItemId.Value] = timeProvider.GetUtcNow();
            boardsFailureReasons.Remove(workItemId.Value);
            var updated = selected with { BoardsWorkItem = boardItem };
            return snapshot with
            {
                BoardsConnection = "Connected",
                Deliveries = snapshot.Deliveries.Select(item => item.WorkItemId == workItemId ? updated : item).ToArray(),
                SelectedDelivery = updated
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsUpstreamFailure(exception))
        {
            boardsAttempts[workItemId.Value] = timeProvider.GetUtcNow();
            var reason = exception switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Not connected (reason: unauthorized)",
                HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "Not connected (reason: forbidden)",
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "Not connected (reason: work item not found)",
                _ => "Not connected (reason: unavailable)"
            };
            boardsFailureReasons[workItemId.Value] = reason;
            return WithBoardsFailure(snapshot, selected, reason);
        }
    }

    private void PruneBoardsCache(IReadOnlyList<DeliverySummary> deliveries)
    {
        var recentIds = deliveries
            .Where(item => item.WorkItemId.HasValue)
            .Select(item => item.WorkItemId!.Value)
            .ToHashSet();
        foreach (var staleId in boardsAttempts.Keys.Where(id => !recentIds.Contains(id)).ToArray())
        {
            boardsAttempts.Remove(staleId);
            boardsLastSuccessAt.Remove(staleId);
            boardsFailureReasons.Remove(staleId);
        }
    }

    private DeliveryStatusSnapshot WithBoardsFailure(
        DeliveryStatusSnapshot snapshot,
        DeliverySummary selected,
        string reason)
    {
        var failedAtUtc = timeProvider.GetUtcNow();
        var failureTime = failedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var staleMessage = selected.BoardsWorkItem is null
            ? $"Azure Boards {reason} at {failureTime}."
            : selected.WorkItemId is int itemId && boardsLastSuccessAt.TryGetValue(itemId, out var lastSuccessAt)
                ? $"Azure Boards {reason} at {failureTime}; showing the last successful projection fetched {lastSuccessAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)} (work item changed {selected.BoardsWorkItem.ChangedAtUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "time unavailable"})."
                : $"Azure Boards {reason} at {failureTime}; showing an older projection with no recorded fetch time.";
        return snapshot with
        {
            BoardsConnection = reason,
            SelectedDelivery = selected,
            IsStale = snapshot.IsStale || selected.BoardsWorkItem is not null,
            UnreachableMessage = staleMessage
        };
    }

    private async Task<GitHubData> FetchGitHubAsync(CancellationToken cancellationToken)
    {
        using var issuesDocument = await FetchGitHubDocumentAsync(
            "repos/cihanduruer/agentic-development/issues?state=all&per_page=50&sort=updated&direction=desc",
            root => root.ValueKind == JsonValueKind.Array,
            cancellationToken);
        var issues = ParseIssues(issuesDocument.RootElement);

        using var pullsDocument = await FetchGitHubDocumentAsync(
            "repos/cihanduruer/agentic-development/pulls?state=all&per_page=50&sort=updated&direction=desc",
            root => root.ValueKind == JsonValueKind.Array,
            cancellationToken);
        var pulls = ParsePullRequests(pullsDocument.RootElement);

        using var runsDocument = await FetchGitHubDocumentAsync(
            "repos/cihanduruer/agentic-development/actions/runs?per_page=100",
            root => root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("workflow_runs", out var workflowRuns) &&
                    workflowRuns.ValueKind == JsonValueKind.Array,
            cancellationToken);
        var runs = ParseRuns(runsDocument.RootElement);

        using var mainRunsDocument = await FetchGitHubDocumentAsync(
            "repos/cihanduruer/agentic-development/actions/runs?branch=main&per_page=100",
            root => root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("workflow_runs", out var workflowRuns) &&
                    workflowRuns.ValueKind == JsonValueKind.Array,
            cancellationToken);
        var knownRunUrls = runs.Select(run => run.Url).ToHashSet(StringComparer.Ordinal);
        runs.AddRange(ParseRuns(mainRunsDocument.RootElement).Where(run => knownRunUrls.Add(run.Url)));
        var deliveries = issues
            .Select(issue => issue with
            {
                PullRequests = pulls.Where(pr =>
                    issue.WorkItemId.HasValue && pr.WorkItemId == issue.WorkItemId).ToArray()
            })
            .ToArray();
        return new GitHubData(deliveries, runs);
    }

    private async Task<JsonDocument> FetchGitHubDocumentAsync(
        string relativeUrl,
        Func<JsonElement, bool> isValidRoot,
        CancellationToken cancellationToken)
    {
        githubResponseCache.TryGetValue(relativeUrl, out var cached);
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        if (cached?.ETag is not null)
        {
            request.Headers.IfNoneMatch.Add(cached.ETag);
        }

        using var response = await SendWithRetryAsync(githubClient, request, false, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (cached?.ETag is null)
            {
                throw new JsonException("GitHub returned not-modified without a cached representation.");
            }

            return JsonDocument.Parse(cached.Body);
        }

        EnsureSuccess(response);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var document = JsonDocument.Parse(body);
        if (!isValidRoot(document.RootElement))
        {
            document.Dispose();
            throw new JsonException("GitHub returned an invalid delivery response.");
        }

        githubResponseCache[relativeUrl] = new GitHubResponseCache(body, response.Headers.ETag);
        return document;
    }

    private async Task<BoardsWorkItemSummary> FetchBoardsItemAsync(int id, CancellationToken cancellationToken)
    {
        var organization = configuration["BoardsStatus:Organization"] ?? "";
        var project = configuration["BoardsStatus:Project"] ?? "";
        var authMode = configuration["BoardsStatus:AuthMode"];
        if (!string.Equals(organization, "ai-enabled-ado-org", StringComparison.Ordinal) ||
            !string.Equals(project, "sample-project", StringComparison.Ordinal) ||
            (authMode is not ("ManagedIdentity" or "Pat")))
        {
            throw new InvalidOperationException("Boards connector configuration is not allowlisted.");
        }

        var api = boardsClient;
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Uri.EscapeDataString(project)}/_apis/wit/workitems/{id}?$expand=relations&api-version=7.1");
        if (authMode == "Pat")
        {
            var pat = configuration["BoardsStatus:Pat"];
            if (string.IsNullOrWhiteSpace(pat))
            {
                throw new InvalidOperationException("Boards PAT is not configured.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        }
        else
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]),
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        using var response = await SendWithRetryAsync(api, request, true, cancellationToken);
        EnsureSuccess(response);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return ProjectBoardsItem(document.RootElement, id, organization, project);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        HttpRequestMessage request,
        bool boards,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedTimeout.CancelAfter(RequestTimeout);
                using var current = await CloneRequestAsync(request, cancellationToken);

                if (!boards)
                {
                    current.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    current.Headers.UserAgent.ParseAdd("AgenticHotelBooking-DeliveryStatus/1.0");
                    var token = configuration["GitHubStatus:Token"];
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        current.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }
                }

                try
                {
                    var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, linkedTimeout.Token);
                    if (attempt == 0 && IsRetryable(response.StatusCode))
                    {
                        response.Dispose();
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                        continue;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            var contentType = response.Content.Headers.ContentType;
                            var body = await ReadBoundedResponseAsync(response.Content, linkedTimeout.Token);
                            response.Content.Dispose();
                            response.Content = new ByteArrayContent(body);
                            response.Content.Headers.ContentType = contentType;
                        }
                        catch
                        {
                            response.Dispose();
                            throw;
                        }
                    }

                    return response;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
            }
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            clone.Content = new StreamContent(await request.Content.ReadAsStreamAsync(cancellationToken));
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumUpstreamResponseBytes)
        {
            throw new InvalidDataException("Delivery upstream response exceeded the size limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumUpstreamResponseBytes)
            {
                throw new InvalidDataException("Delivery upstream response exceeded the size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 ||
        (int)status >= 500;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Delivery upstream returned {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }

    private bool BoardsIsConfigured() =>
        string.Equals(configuration["BoardsStatus:Organization"], "ai-enabled-ado-org", StringComparison.Ordinal) &&
        string.Equals(configuration["BoardsStatus:Project"], "sample-project", StringComparison.Ordinal) &&
        configuration["BoardsStatus:AuthMode"] is "ManagedIdentity" or "Pat" &&
        (configuration["BoardsStatus:AuthMode"] != "Pat" || !string.IsNullOrWhiteSpace(configuration["BoardsStatus:Pat"]));

    private string BoardsNotConnectedReason()
    {
        if (!string.Equals(configuration["BoardsStatus:Organization"], "ai-enabled-ado-org", StringComparison.Ordinal) ||
            !string.Equals(configuration["BoardsStatus:Project"], "sample-project", StringComparison.Ordinal))
        {
            return "Not connected (reason: organization/project configuration is missing or not allowlisted)";
        }

        if (configuration["BoardsStatus:AuthMode"] is not ("ManagedIdentity" or "Pat"))
        {
            return "Not connected (reason: authentication mode is not configured)";
        }

        return "Not connected (reason: PAT is not configured)";
    }

    private static bool IsUpstreamFailure(Exception exception) =>
        exception is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException or
            InvalidOperationException or IOException or InvalidDataException or AuthenticationFailedException;

    private static DeliveryStatusSnapshot Select(DeliveryStatusSnapshot snapshot, int? workItemId)
    {
        var scrubbed = snapshot.Deliveries
            .Select(item => item.WorkItemId == workItemId ? item : item with { BoardsWorkItem = null })
            .ToArray();
        if (workItemId is null)
        {
            return snapshot with { Deliveries = scrubbed, SelectedDelivery = null };
        }

        var selected = scrubbed.FirstOrDefault(item => item.WorkItemId == workItemId)
            ?? throw new KeyNotFoundException("The work item is not present in recent GitHub deliveries.");
        return snapshot with { Deliveries = scrubbed, SelectedDelivery = selected };
    }

    private static List<IssueData> ParseIssues(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Invalid issues response.");
        }

        var items = new List<IssueData>();
        foreach (var element in root.EnumerateArray())
        {
            if (element.TryGetProperty("pull_request", out _) ||
                !ReadInt(element, "number", out var number) ||
                !ReadString(element, "title", out var title) ||
                !ReadString(element, "html_url", out var url) ||
                !IsRepositoryUrl(url) ||
                !TrustedAuthorAssociations.Contains(ReadOptionalString(element, "author_association"), StringComparer.Ordinal) ||
                !ReadDate(element, "updated_at", out var updated))
            {
                continue;
            }

            var workItemId = CanonicalIssueWorkItemId(title, ReadOptionalString(element, "body"));
            if (workItemId is null)
            {
                continue;
            }

            items.Add(new IssueData(number, workItemId, title, url, updated, []));
        }

        return items
            .GroupBy(item => item.WorkItemId)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToList();
    }

    private static int? CanonicalIssueWorkItemId(string title, string body)
    {
        var titleMatch = Regex.Match(title, @"^\[AB#([1-9][0-9]{0,9})\](?:\s|$)", RegexOptions.CultureInvariant);
        var bodyMatch = Regex.Match(
            body,
            @"(?m)^\s*Azure Boards work item:\s*\[AB#([1-9][0-9]{0,9})\]\(https://dev\.azure\.com/" +
                Regex.Escape(BoardsOrganization) + "/" + Regex.Escape(BoardsProject) +
                @"/_workitems/edit/([1-9][0-9]{0,9})\)\s*$",
            RegexOptions.CultureInvariant);
        var ids = new List<string>();
        if (titleMatch.Success)
        {
            ids.Add(titleMatch.Groups[1].Value);
        }

        if (bodyMatch.Success && bodyMatch.Groups[1].Value == bodyMatch.Groups[2].Value)
        {
            ids.Add(bodyMatch.Groups[1].Value);
        }

        var distinct = ids.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 1 &&
            int.TryParse(distinct[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
    }

    private static bool IsSameRepositoryMainPullRequest(JsonElement element) =>
        element.TryGetProperty("base", out var baseElement) &&
        baseElement.TryGetProperty("ref", out var baseRef) &&
        baseRef.ValueKind == JsonValueKind.String &&
        baseRef.GetString() == "main" &&
        element.TryGetProperty("head", out var headElement) &&
        headElement.TryGetProperty("repo", out var headRepo) &&
        headRepo.ValueKind == JsonValueKind.Object &&
        headRepo.TryGetProperty("full_name", out var fullName) &&
        fullName.ValueKind == JsonValueKind.String &&
        string.Equals(fullName.GetString(), RepositoryFullName, StringComparison.Ordinal);

    private static List<PullRequestData> ParsePullRequests(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Invalid pull-request response.");
        }

        var result = new List<PullRequestData>();
        foreach (var element in root.EnumerateArray())
        {
            if (!ReadInt(element, "number", out var number) ||
                !ReadString(element, "title", out var title) ||
                !ReadString(element, "html_url", out var url) ||
                !IsRepositoryUrl(url) ||
                !IsSameRepositoryMainPullRequest(element) ||
                !ReadDate(element, "updated_at", out var updated))
            {
                continue;
            }

            var workItemId = FindWorkItemId(title + " " + ReadOptionalString(element, "body"));
            if (workItemId is null)
            {
                continue;
            }

            var merged = (element.TryGetProperty("merged", out var mergedElement) &&
                mergedElement.ValueKind == JsonValueKind.True) ||
                (element.TryGetProperty("merged_at", out var mergedAt) &&
                mergedAt.ValueKind == JsonValueKind.String);
            if (ReadOptionalString(element, "state") != "open" && !merged)
            {
                continue;
            }
            var headSha = element.TryGetProperty("head", out var head) &&
                head.TryGetProperty("sha", out var sha) &&
                sha.ValueKind == JsonValueKind.String
                    ? sha.GetString() ?? ""
                    : "";
            var mergeSha = merged ? ReadOptionalString(element, "merge_commit_sha") : "";
            result.Add(new PullRequestData(number, workItemId, title, url, updated, merged)
            {
                IsDraft = element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                HeadSha = headSha.Length == 40 && headSha.All(Uri.IsHexDigit) ? headSha : "",
                MergeCommitSha = mergeSha.Length == 40 && mergeSha.All(Uri.IsHexDigit) ? mergeSha : ""
            });
        }

        return result;
    }

    private static List<RunData> ParseRuns(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("workflow_runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Invalid workflow-runs response.");
        }

        var result = new List<RunData>();
        foreach (var element in runs.EnumerateArray())
        {
            var name = ReadOptionalString(element, "name");
            if (ReadOptionalString(element, "path") == "dynamic/copilot-swe-agent/copilot")
            {
                name = "Copilot cloud agent";
            }
            else if (!WorkflowNames.Contains(name, StringComparer.Ordinal))
            {
                name = ReadOptionalString(element, "workflow_name");
            }

            if (!WorkflowNames.Contains(name, StringComparer.Ordinal) ||
                !ReadString(element, "html_url", out var url) ||
                !IsRepositoryUrl(url) ||
                !ReadDate(element, "created_at", out var created))
            {
                continue;
            }

            var status = ReadOptionalString(element, "status");
            var conclusion = ReadOptionalString(element, "conclusion");
            var updated = ReadDate(element, "updated_at", out var date) ? date : created;
            long? duration = null;
            if (ReadDate(element, "run_started_at", out var started) &&
                ReadDate(element, "updated_at", out var ended) &&
                status == "completed")
            {
                duration = Math.Max(0, (long)(ended - started).TotalMilliseconds);
            }

            var sha = ReadOptionalString(element, "head_sha");
            if (sha.Length != 40 || !sha.All(Uri.IsHexDigit))
            {
                sha = "";
            }

            var pullRequestNumbers = new HashSet<int>();
            if (element.TryGetProperty("pull_requests", out var prs) && prs.ValueKind == JsonValueKind.Array)
            {
                foreach (var pr in prs.EnumerateArray())
                {
                    if (ReadInt(pr, "number", out var prNumber))
                    {
                        pullRequestNumbers.Add(prNumber);
                    }
                }
            }

            var workItemId = FindWorkItemId(ReadOptionalString(element, "display_title") + " " +
                ReadOptionalString(element, "name"));
            result.Add(new RunData(name, ToStatus(status, conclusion), updated, duration, url, sha, pullRequestNumbers)
            {
                WorkItemId = workItemId
            });
        }

        return result;
    }

    private static BoardsWorkItemSummary ProjectBoardsItem(
        JsonElement root,
        int requestedId,
        string organization,
        string project)
    {
        if (!root.TryGetProperty("id", out var idValue) ||
            !idValue.TryGetInt32(out var id) || id != requestedId ||
            !root.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Invalid Boards item response.");
        }

        var title = Limit(Field(fields, "System.Title"), 500);
        var state = Limit(Field(fields, "System.State"), 100);
        var type = Limit(Field(fields, "System.WorkItemType"), 100);
        if (title.Length == 0 || state.Length == 0 || type.Length == 0)
        {
            throw new JsonException("Boards item is missing projected fields.");
        }

        var tags = Field(fields, "System.Tags")
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => Limit(tag, 100))
            .Take(20)
            .ToArray();
        DateTimeOffset? changed = DateTimeOffset.TryParse(
            Field(fields, "System.ChangedDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
        var links = new List<DeliveryLinkSummary>();
        if (root.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in relations.EnumerateArray().Take(100))
            {
                var relationType = ReadOptionalString(relation, "rel");
                if (relationType is not ("ArtifactLink" or "Hyperlink" or "ExternalLink") ||
                    !relation.TryGetProperty("url", out var urlElement) ||
                    urlElement.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri) ||
                    uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                    !IsAllowedBoardsLink(uri, organization, project))
                {
                    continue;
                }

                var label = relationType switch
                {
                    "ArtifactLink" => "Delivery artifact",
                    "Hyperlink" => "Work item link",
                    "ExternalLink" => "External work item link",
                    _ => "Work item link"
                };
                if (!links.Any(link => link.Url == uri.AbsoluteUri))
                {
                    links.Add(new DeliveryLinkSummary(label, uri.AbsoluteUri));
                }

                if (links.Count == 20)
                {
                    break;
                }
            }
        }

        var url = $"https://dev.azure.com/{organization}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}";
        return new BoardsWorkItemSummary(id, title, state, type, tags, changed, url, links);
    }

    private static List<DeliveryStageSummary> BuildStages(
        IssueData issue,
        PullRequestData? pullRequest,
        IReadOnlyList<RunData> runs)
    {
        var postMergeWorkflows = new[] { "Deploy development", "Sync development lifecycle", "Release proposal" };
        var matched = runs.Where(run =>
            (pullRequest is not null && run.HeadSha.Length > 0 &&
                (postMergeWorkflows.Contains(run.Name, StringComparer.Ordinal)
                    ? pullRequest.Merged && run.HeadSha == pullRequest.MergeCommitSha
                    : run.HeadSha == pullRequest.HeadSha)) ||
            (run.Name == "Agentic intake" && run.WorkItemId == issue.WorkItemId))
            .GroupBy(run => run.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(run => run.UpdatedAt).First(), StringComparer.Ordinal);

        DeliveryStageSummary RunStage(string stage, string workflow) =>
            matched.TryGetValue(workflow, out var run)
                ? new(stage, run.Status, run.UpdatedAt, run.DurationMilliseconds, run.Url, EmptyToNull(run.HeadSha))
                : new(stage, "Not started", null, null, null, null);

        var results = new List<DeliveryStageSummary>
        {
            matched.TryGetValue("Agentic intake", out var intakeRun)
                ? ToStage(StageNames[0], intakeRun)
                : new(StageNames[0], "Passed", issue.UpdatedAt, null, issue.Url, null),
            matched.TryGetValue("Copilot cloud agent", out var agentRun)
                ? ToStage(StageNames[1], agentRun)
                : pullRequest is null
                    ? new(StageNames[1], "Waiting", issue.UpdatedAt, null, issue.Url, null)
                    : new(StageNames[1], "Passed", pullRequest.UpdatedAt, null, pullRequest.Url, pullRequest.HeadSha),
            new(StageNames[2], pullRequest is null ? "Not started" : pullRequest.Merged ? "Passed" : "Running",
                pullRequest?.UpdatedAt, null, pullRequest?.Url, pullRequest?.HeadSha),
            RunStage(StageNames[3], "PR validation"),
            RunStage(StageNames[4], "Hotel code review"),
            CombineQaEvidence(StageNames[5], matched),
            new(StageNames[6], pullRequest?.Merged == true ? "Passed" : pullRequest is null ? "Not started" : "Waiting",
                pullRequest?.UpdatedAt, null, pullRequest?.Url, pullRequest?.HeadSha),
            RunStage(StageNames[7], "Deploy development"),
            RunStage(StageNames[8], "Sync development lifecycle"),
            RunStage(StageNames[9], "Release proposal"),
            new(StageNames[10],
                matched.TryGetValue("Release proposal", out var proposal) && proposal.Status == "Passed" ? "Waiting" : "Not started",
                null, null, null, null)
        };

        return results;
    }

    private static string OverallStatus(IReadOnlyList<DeliveryStageSummary> stages)
    {
        if (stages.Any(stage => stage.Status == "Failed"))
        {
            return "Failed";
        }

        if (stages.Any(stage => stage.Status == "Running"))
        {
            return "Running";
        }

        if (stages.Any(stage => stage.Status == "Waiting"))
        {
            return "Waiting";
        }

        var current = stages.FirstOrDefault(stage =>
            stage.Status == "Not started");
        if (current is null)
        {
            return "Passed";
        }

        return current.Status == "Not started" && stages.TakeWhile(stage => stage != current).Any(stage => stage.Status == "Passed")
            ? "Waiting"
            : current.Status;
    }

    private static DeliveryStageSummary ToStage(string name, RunData run) =>
        new(name, run.Status, run.UpdatedAt, run.DurationMilliseconds, run.Url, EmptyToNull(run.HeadSha));

    private static DeliveryStageSummary CombineQaEvidence(string name, IReadOnlyDictionary<string, RunData> matched)
    {
        var evidence = QaWorkflowNames
            .Where(matched.ContainsKey)
            .Select(workflow => matched[workflow])
            .ToArray();
        if (evidence.Length == 0)
        {
            return new(name, "Not started", null, null, null, null);
        }

        var status = evidence.Any(run => run.Status == "Failed") ? "Failed" :
            evidence.Any(run => run.Status == "Running") ? "Running" :
            evidence.Any(run => run.Status == "Waiting") ? "Waiting" :
            evidence.Any(run => run.Status == "Skipped") ? "Skipped" :
            evidence.Length == QaWorkflowNames.Length && evidence.All(run => run.Status == "Passed") ? "Passed" : "Waiting";
        var latest = evidence.OrderByDescending(run => run.UpdatedAt).First();
        long? duration = evidence.All(run => run.DurationMilliseconds.HasValue)
            ? evidence.Sum(run => run.DurationMilliseconds!.Value)
            : null;
        return new DeliveryStageSummary(name, status, latest.UpdatedAt, duration, latest.Url, EmptyToNull(latest.HeadSha))
        {
            RelatedEvidence = evidence.Select(run => new DeliveryEvidenceLink(run.Name, run.Url)).ToArray()
        };
    }

    private static string ToStatus(string status, string conclusion) => status switch
    {
        "in_progress" or "queued" or "requested" or "waiting" or "pending" => "Running",
        "completed" when conclusion == "success" => "Passed",
        "completed" when conclusion is "failure" or "cancelled" or "timed_out" or "action_required" or "stale" or "startup_failure" => "Failed",
        "completed" when conclusion is "skipped" or "neutral" => "Skipped",
        _ => "Waiting"
    };

    private static int? FindWorkItemId(string text)
    {
        var ids = WorkItemPattern.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return ids.Length == 1 &&
            int.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
    }

    private static string Field(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string ReadOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool ReadString(JsonElement element, string name, out string value)
    {
        value = ReadOptionalString(element, name);
        return value.Length > 0;
    }

    private static bool ReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.TryGetInt32(out value) &&
            value > 0;
    }

    private static bool ReadDate(JsonElement element, string name, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(ReadOptionalString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    private static bool IsRepositoryUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 &&
        uri.Host == "github.com" &&
        uri.AbsolutePath.StartsWith("/cihanduruer/agentic-development/", StringComparison.Ordinal);

    private static bool IsAllowedBoardsLink(Uri uri, string organization, string project) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        ((uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/cihanduruer/agentic-development/", StringComparison.Ordinal)) ||
         (uri.Host == "dev.azure.com" && uri.AbsolutePath.StartsWith($"/{organization}/{project}/", StringComparison.Ordinal)));

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private sealed record IssueData(
        int Number,
        int? WorkItemId,
        string Title,
        string Url,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<PullRequestData> PullRequests);

    private sealed record PullRequestData(
        int Number,
        int? WorkItemId,
        string Title,
        string Url,
        DateTimeOffset UpdatedAt,
        bool Merged)
    {
        public bool IsDraft { get; init; }

        public string HeadSha { get; init; } = "";

        public string MergeCommitSha { get; init; } = "";
    }

    private sealed record RunData(
        string Name,
        string Status,
        DateTimeOffset UpdatedAt,
        long? DurationMilliseconds,
        string Url,
        string HeadSha,
        IReadOnlySet<int> PullRequestNumbers)
    {
        public int? WorkItemId { get; init; }
    }

    private sealed record GitHubData(IReadOnlyList<IssueData> Issues, IReadOnlyList<RunData> Runs);

    private sealed record GitHubResponseCache(byte[] Body, EntityTagHeaderValue? ETag);

    public void Dispose()
    {
        refreshLock.Dispose();
        githubClient.Dispose();
        boardsClient.Dispose();
    }
}
