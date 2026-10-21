using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgenticHotelBooking.Infrastructure;
using Azure.Core;
using Microsoft.Extensions.Configuration;

namespace AgenticHotelBooking.UnitTests;

public sealed class DeliveryStatusServiceTests
{
    private const int WorkItemId = 965;
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string FakeAccessToken = "fake-credential-token-not-a-secret";
    private const string FakeGitHubToken = "fake-readonly-github-token";

    [Fact]
    public async Task OlderHeadRunCannotPassCurrentRevisionEvenWhenPullRequestNumberMatches()
    {
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, """
                    {"workflow_runs":[{
                      "name":"PR validation","status":"completed","conclusion":"success",
                      "head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "html_url":"https://github.com/cihanduruer/agentic-development/actions/runs/999",
                      "created_at":"2026-10-13T10:20:00Z","updated_at":"2026-10-13T10:25:00Z",
                      "pull_requests":[{"number":43}]
                    }]}
                    """)
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler);
        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);
        var delivery = Assert.Single(snapshot.Deliveries, item => item.WorkItemId == WorkItemId);
        Assert.Equal("Not started", delivery.Stages.Single(stage => stage.Name == "PR validation").Status);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("skipped")]
    public async Task OneQaWorkflowCannotStandInForBothRequiredEvidenceSources(string conclusion)
    {
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, $$"""
                    {"workflow_runs":[{
                      "name":"Hotel browser evidence","status":"completed","conclusion":"{{conclusion}}",
                      "head_sha":"{{HeadSha}}",
                      "html_url":"https://github.com/cihanduruer/agentic-development/actions/runs/999",
                      "created_at":"2026-10-13T10:20:00Z","updated_at":"2026-10-13T10:25:00Z",
                      "pull_requests":[{"number":43}]
                    }]}
                    """)
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler);
        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);
        var delivery = Assert.Single(snapshot.Deliveries, item => item.WorkItemId == WorkItemId);
        Assert.NotEqual("Passed", delivery.Stages.Single(stage => stage.Name == "QA evidence").Status);
    }

    [Fact]
    public async Task SelectedDeliveryProjectsOnlyAllowlistedBoardsFieldsAndTypedLinks()
    {
        var handler = new DeliveryHandler();
        using var service = CreateService(handler, githubToken: FakeGitHubToken);

        var snapshot = await service.GetStatusAsync(WorkItemId, CancellationToken.None);
        var selected = Assert.IsType<AgenticHotelBooking.Application.DeliverySummary>(snapshot.SelectedDelivery);
        var board = Assert.IsType<AgenticHotelBooking.Application.BoardsWorkItemSummary>(selected.BoardsWorkItem);
        var json = JsonSerializer.Serialize(snapshot);

        Assert.Equal(WorkItemId, board.Id);
        Assert.Equal("Active", board.State);
        Assert.Equal("User Story", board.Type);
        Assert.Equal(["delivery-evidence", "github-synced"], board.Tags);
        Assert.Single(board.Links);
        Assert.Equal("Delivery artifact", board.Links[0].Label);
        Assert.Contains("Passed", selected.Stages.Single(stage => stage.Name == "PR validation").Status);
        Assert.Equal(30_000, selected.Stages.Single(stage => stage.Name == "PR validation").DurationMilliseconds);
        Assert.Equal("Passed", selected.Stages.Single(stage => stage.Name == "GitHub issue and cloud agent").Status);
        Assert.Equal(2, selected.Stages.Single(stage => stage.Name == "QA evidence").RelatedEvidence.Count);
        Assert.Equal("Connected", snapshot.BoardsConnection);
        Assert.DoesNotContain("FakeCredential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakeAccessToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeGitHubToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-body", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assignedTo", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.BoardsRequestCount);
        Assert.Equal(4, handler.GitHubRequestCount);
        Assert.Equal("Bearer " + FakeGitHubToken, handler.LastGitHubAuthorization);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains(handler.Requests, request => request.Path.Contains("/_apis/wit/workitems/965", StringComparison.Ordinal));

        var unselected = await service.GetStatusAsync(null, CancellationToken.None);
        Assert.Null(unselected.SelectedDelivery);
        Assert.Equal(4, handler.GitHubRequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Not connected (reason: unauthorized)")]
    [InlineData(HttpStatusCode.Forbidden, "Not connected (reason: forbidden)")]
    [InlineData(HttpStatusCode.NotFound, "Not connected (reason: work item not found)")]
    public async Task BoardsAuthorizationAndNotFoundFailuresReturnSanitizedExplicitState(
        HttpStatusCode status,
        string expectedConnection)
    {
        var handler = new DeliveryHandler(boardsResponse: _ => JsonResponse(
            status,
            """{"message":"private upstream error body","token":"untrusted-token"}"""));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(WorkItemId, CancellationToken.None);

        Assert.Equal(expectedConnection, snapshot.BoardsConnection);
        Assert.Null(snapshot.SelectedDelivery!.BoardsWorkItem);
        var body = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("private upstream", body, StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoardsTimeoutRetriesOnlyOnceAndDoesNotReturnUpstreamBody()
    {
        var handler = new DeliveryHandler(boardsResponse: _ => throw new OperationCanceledException("timeout"));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(WorkItemId, CancellationToken.None);

        Assert.Equal("Not connected (reason: unavailable)", snapshot.BoardsConnection);
        Assert.Equal(2, handler.BoardsRequestCount);
        Assert.DoesNotContain("timeout", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedBoardsPayloadIsReportedWithoutReturningRawPayload()
    {
        var handler = new DeliveryHandler(boardsResponse: _ => JsonResponse(HttpStatusCode.OK, "{ malformed"));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(WorkItemId, CancellationToken.None);

        Assert.Equal("Not connected (reason: unavailable)", snapshot.BoardsConnection);
        Assert.Null(snapshot.SelectedDelivery!.BoardsWorkItem);
    }

    [Fact]
    public async Task UnconfiguredBoardsConnectorUsesExactNotConnectedReasonAndDoesNotCallBoards()
    {
        var handler = new DeliveryHandler();
        using var service = CreateService(handler, configuredBoards: false);

        var snapshot = await service.GetStatusAsync(WorkItemId, CancellationToken.None);

        Assert.Equal(
            "Not connected (reason: organization/project configuration is missing or not allowlisted)",
            snapshot.BoardsConnection);
        Assert.Null(snapshot.SelectedDelivery!.BoardsWorkItem);
        Assert.Equal(0, handler.BoardsRequestCount);
    }

    [Fact]
    public async Task RecentMergedPullRequestIsIncludedAndReportedAsPassed()
    {
        var handler = new DeliveryHandler();
        using var service = CreateService(handler, configuredBoards: false);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);
        var mergedDelivery = Assert.Single(snapshot.Deliveries, delivery => delivery.WorkItemId == 966);

        Assert.Equal(44, mergedDelivery.PullRequestNumber);
        Assert.Equal("Passed", mergedDelivery.Stages.Single(stage => stage.Name == "Pull request").Status);
        Assert.Equal("Passed", mergedDelivery.Stages.Single(stage => stage.Name == "Merge").Status);
    }

    [Fact]
    public async Task NonPositiveGitHubPullRequestNumberIsIgnored()
    {
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, $$"""
                    [{
                      "number": 0,
                      "title": "AB#{{WorkItemId}}: invalid pull request",
                      "body": "AB#{{WorkItemId}}",
                      "html_url": "https://github.com/cihanduruer/agentic-development/pull/43",
                      "updated_at": "2026-10-13T10:10:00Z",
                      "state": "open",
                      "merged": false
                    }]
                    """)
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler, configuredBoards: false);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);
        var delivery = Assert.Single(snapshot.Deliveries, item => item.WorkItemId == WorkItemId);

        Assert.Null(delivery.PullRequestNumber);
        Assert.Equal("Not started", delivery.Stages.Single(stage => stage.Name == "Pull request").Status);
    }

    [Fact]
    public async Task PatFallbackUsesOnlyConfiguredBasicHeaderAndNeverReturnsPat()
    {
        const string fakePat = "test-only-fake-pat";
        var handler = new DeliveryHandler();
        using var service = CreateService(handler, boardsAuthMode: "Pat", boardsPat: fakePat);

        var snapshot = await service.GetStatusAsync(WorkItemId, CancellationToken.None);
        var body = JsonSerializer.Serialize(snapshot);

        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($":{fakePat}")),
            handler.LastBoardsAuthorization);
        Assert.DoesNotContain(fakePat, body, StringComparison.Ordinal);
        Assert.Equal("Connected", snapshot.BoardsConnection);
    }

    [Fact]
    public async Task GitHubMalformedResponseReturnsNoFabricatedDelivery()
    {
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, "{ malformed")
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);

        Assert.Empty(snapshot.Deliveries);
        Assert.True(snapshot.IsStale);
        Assert.Contains("unreachable", snapshot.UnreachableMessage, StringComparison.OrdinalIgnoreCase);
        await service.GetStatusAsync(null, CancellationToken.None);
        Assert.Equal(1, handler.GitHubRequestCount);
    }

    [Fact]
    public async Task PostMergeRunsAreAttributedByMergeCommitAndMissingIntakeRunIsDerivedFromIssue()
    {
        const string mergeSha = "fedcba9876543210fedcba9876543210fedcba98";
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? request.RequestUri.Query.Contains("branch=main", StringComparison.Ordinal)
                    ? JsonResponse(HttpStatusCode.OK, $$"""
                    {"workflow_runs":[{
                      "name": "Deploy development",
                      "status": "completed",
                      "conclusion": "success",
                      "head_sha": "{{mergeSha}}",
                      "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/888",
                      "created_at": "2026-10-13T10:20:00Z",
                      "run_started_at": "2026-10-13T10:20:00Z",
                      "updated_at": "2026-10-13T10:25:00Z",
                      "pull_requests": []
                    }]}
                    """)
                    : JsonResponse(HttpStatusCode.OK, """{"workflow_runs":[]}""")
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);

        var delivery = Assert.Single(snapshot.Deliveries, item => item.WorkItemId == 966);
        Assert.Equal("Passed", delivery.Stages.Single(stage => stage.Name == "Development deployment").Status);
        Assert.Equal("Passed", delivery.Stages.Single(stage => stage.Name == "Boards intake").Status);
        Assert.Equal("Passed", delivery.Stages.Single(stage => stage.Name == "GitHub issue and cloud agent").Status);
        var other = Assert.Single(snapshot.Deliveries, item => item.WorkItemId == WorkItemId);
        Assert.Equal("Not started", other.Stages.Single(stage => stage.Name == "Development deployment").Status);
    }

    [Fact]
    public async Task MergeCommitRunsOfPrScopedWorkflowsDoNotReplaceThePullRequestEvidence()
    {
        const string mergeSha = "fedcba9876543210fedcba9876543210fedcba98";
        const string prHeadSha = "abcdef0123456789abcdef0123456789abcdef01";
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, $$"""
                    {"workflow_runs":[{
                      "name": "PR validation", "status": "completed", "conclusion": "success",
                      "head_sha": "{{prHeadSha}}",
                      "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/901",
                      "created_at": "2026-10-13T10:10:00Z", "updated_at": "2026-10-13T10:12:00Z",
                      "pull_requests": [{ "number": 44 }]
                    }, {
                      "name": "PR validation", "status": "completed", "conclusion": "failure",
                      "head_sha": "{{mergeSha}}",
                      "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/902",
                      "created_at": "2026-10-13T10:20:00Z", "updated_at": "2026-10-13T10:25:00Z",
                      "pull_requests": []
                    }]}
                    """)
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);

        var delivery = Assert.Single(snapshot.Deliveries, item => item.WorkItemId == 966);
        Assert.Equal("Passed", delivery.Stages.Single(stage => stage.Name == "PR validation").Status);
    }

    [Fact]
    public async Task OversizedUpstreamResponseDegradesToUnavailableInsteadOfThrowing()
    {
        var oversized = new string('x', 5 * 1024 * 1024);
        var handler = new DeliveryHandler(githubResponse: _ =>
            JsonResponse(HttpStatusCode.OK, $$"""[{"body":"{{oversized}}"}]"""));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);

        Assert.True(snapshot.IsStale);
        Assert.Empty(snapshot.Deliveries);
    }

    [Fact]
    public async Task GitHubRateLimitServesMarkedStaleLastGoodSnapshot()
    {
        var time = new ManualTimeProvider();
        var issueReads = 0;
        var handler = new DeliveryHandler(githubResponse: request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal) && ++issueReads > 1)
            {
                return JsonResponse(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""");
            }

            return DeliveryHandler.StandardGitHubResponse(request);
        });
        using var service = CreateService(handler, timeProvider: time, githubToken: FakeGitHubToken);
        var original = await service.GetStatusAsync(null, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(46));

        var stale = await service.GetStatusAsync(null, CancellationToken.None);

        Assert.Equal(2, original.Deliveries.Count);
        Assert.Equal(2, stale.Deliveries.Count);
        Assert.True(stale.IsStale);
        Assert.Contains("last successful update", stale.UnreachableMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.Deliveries[0].GitHubIssueUrl, stale.Deliveries[0].GitHubIssueUrl);
        Assert.Equal(5, handler.GitHubRequestCount);
        await service.GetStatusAsync(null, CancellationToken.None);
        Assert.Equal(5, handler.GitHubRequestCount);
    }

    [Fact]
    public async Task GitHubUsesConditionalRequestsAndReusesCachedBodiesAfterNotModified()
    {
        var time = new ManualTimeProvider();
        var handler = new DeliveryHandler(githubResponse: request =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = DeliveryHandler.StandardGitHubResponse(request);
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return response;
        });
        using var service = CreateService(handler, configuredBoards: false, timeProvider: time, githubToken: FakeGitHubToken);

        var first = await service.GetStatusAsync(null, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(46));
        var refreshed = await service.GetStatusAsync(null, CancellationToken.None);

        Assert.Equal(8, handler.GitHubRequestCount);
        Assert.Equal(4, handler.ConditionalGitHubRequestCount);
        Assert.Equal(2, refreshed.Deliveries.Count);
        Assert.Equal(first.Deliveries.Select(delivery => delivery.GitHubIssueUrl),
            refreshed.Deliveries.Select(delivery => delivery.GitHubIssueUrl));
    }

    [Fact]
    public async Task OnlyTheRequestedDeliveryCarriesBoardsProjection()
    {
        var handler = new DeliveryHandler();
        using var service = CreateService(handler);

        var selected = await service.GetStatusAsync(WorkItemId, CancellationToken.None);
        Assert.NotNull(selected.SelectedDelivery!.BoardsWorkItem);

        var unselected = await service.GetStatusAsync(null, CancellationToken.None);
        Assert.All(unselected.Deliveries, delivery => Assert.Null(delivery.BoardsWorkItem));

        var other = await service.GetStatusAsync(966, CancellationToken.None);
        Assert.Null(other.Deliveries.Single(delivery => delivery.WorkItemId == WorkItemId).BoardsWorkItem);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("startup_failure")]
    public void TerminalUnsuccessfulRunConclusionsAreFailed(string conclusion)
    {
        var method = typeof(DeliveryStatusService).GetMethod(
            "ToStatus",
            global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Static)!;

        Assert.Equal("Failed", method.Invoke(null, ["completed", conclusion]));
    }

    [Fact]
    public async Task StaleMessageKeepsTheTrueLastSuccessfulTimestamp()
    {
        var time = new ManualTimeProvider();
        var issueReads = 0;
        var handler = new DeliveryHandler(githubResponse: request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal) && ++issueReads > 1)
            {
                return JsonResponse(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""");
            }

            return DeliveryHandler.StandardGitHubResponse(request);
        });
        using var service = CreateService(handler, timeProvider: time, githubToken: FakeGitHubToken);
        await service.GetStatusAsync(null, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(46));
        await service.GetStatusAsync(null, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(46));

        var second = await service.GetStatusAsync(null, CancellationToken.None);

        Assert.Contains("2026-10-13 10:00:00 UTC", second.UnreachableMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicCopilotCloudAgentRunIsCanonicalizedByWorkflowPath()
    {
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, $$"""
                    {"workflow_runs":[{
                      "name": "Running Copilot cloud agent",
                      "path": "dynamic/copilot-swe-agent/copilot",
                      "status": "completed",
                      "conclusion": "success",
                      "head_sha": "{{HeadSha}}",
                      "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/777",
                      "created_at": "2026-10-13T10:00:00Z",
                      "run_started_at": "2026-10-13T10:00:00Z",
                      "updated_at": "2026-10-13T10:02:00Z",
                      "pull_requests": [{ "number": 43 }]
                    }]}
                    """)
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(WorkItemId, CancellationToken.None);

        var stage = snapshot.SelectedDelivery!.Stages.Single(item => item.Name == "GitHub issue and cloud agent");
        Assert.Equal("Passed", stage.Status);
    }

    [Fact]
    public async Task UntrustedIssuesAndForkOrNonMainPullRequestsAreNotDeliveries()
    {
        var handler = new DeliveryHandler(githubResponse: request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/issues", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, $$"""
                    [{
                      "number": 70, "title": "[AB#{{WorkItemId}}] spoof", "body": "x", "author_association": "NONE",
                      "html_url": "https://github.com/cihanduruer/agentic-development/issues/70", "updated_at": "2026-10-13T10:00:00Z"
                    }, {
                      "number": 71, "title": "mentions AB#{{WorkItemId}} loosely", "body": "AB#{{WorkItemId}}", "author_association": "OWNER",
                      "html_url": "https://github.com/cihanduruer/agentic-development/issues/71", "updated_at": "2026-10-13T10:00:00Z"
                    }]
                    """);
            }

            if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, $$"""
                    [{
                      "number": 72, "title": "AB#{{WorkItemId}} fork", "body": "AB#{{WorkItemId}}", "state": "open",
                      "html_url": "https://github.com/cihanduruer/agentic-development/pull/72", "updated_at": "2026-10-13T10:00:00Z",
                      "base": { "ref": "main" }, "head": { "sha": "{{HeadSha}}", "repo": { "full_name": "attacker/agentic-development" } }
                    }, {
                      "number": 73, "title": "AB#{{WorkItemId}} other base", "body": "AB#{{WorkItemId}}", "state": "open",
                      "html_url": "https://github.com/cihanduruer/agentic-development/pull/73", "updated_at": "2026-10-13T10:00:00Z",
                      "base": { "ref": "release" }, "head": { "sha": "{{HeadSha}}", "repo": { "full_name": "cihanduruer/agentic-development" } }
                    }]
                    """);
            }

            return DeliveryHandler.StandardGitHubResponse(request);
        });
        using var service = CreateService(handler);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);

        Assert.Empty(snapshot.Deliveries);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetStatusAsync(WorkItemId, CancellationToken.None));
        Assert.Equal(0, handler.BoardsRequestCount);
    }

    [Fact]
    public async Task WorkflowRunsForADifferentPullRequestWithTheSameWorkItemTitleAreNotAttributed()
    {
        var handler = new DeliveryHandler(githubResponse: request =>
            request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, $$"""
                    {"workflow_runs":[{
                      "name": "PR validation", "status": "completed", "conclusion": "success",
                      "display_title": "AB#{{WorkItemId}} other pull request",
                      "head_sha": "fedcba9876543210fedcba9876543210fedcba98",
                      "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/900",
                      "created_at": "2026-10-13T10:00:00Z", "updated_at": "2026-10-13T10:01:00Z",
                      "pull_requests": [{ "number": 99 }]
                    }]}
                    """)
                : DeliveryHandler.StandardGitHubResponse(request));
        using var service = CreateService(handler, configuredBoards: false);

        var snapshot = await service.GetStatusAsync(null, CancellationToken.None);

        var delivery = Assert.Single(snapshot.Deliveries, item => item.WorkItemId == WorkItemId);
        Assert.Equal("Not started", delivery.Stages.Single(stage => stage.Name == "PR validation").Status);
    }

    [Fact]
    public async Task AnonymousGitHubModeRefreshesNoMoreOftenThanItsRateBudgetAllows()
    {
        var time = new ManualTimeProvider();
        var handler = new DeliveryHandler();
        using var service = CreateService(handler, configuredBoards: false, timeProvider: time);

        await service.GetStatusAsync(null, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(3));
        await service.GetStatusAsync(null, CancellationToken.None);
        Assert.Equal(4, handler.GitHubRequestCount);

        time.Advance(TimeSpan.FromMinutes(2));
        await service.GetStatusAsync(null, CancellationToken.None);
        Assert.Equal(8, handler.GitHubRequestCount);
    }

    [Fact]
    public async Task BoardsRequestUsesOnlyExpandRelationsWithoutFieldsParameter()
    {
        var handler = new DeliveryHandler();
        using var service = CreateService(handler);

        await service.GetStatusAsync(WorkItemId, CancellationToken.None);

        var boardsRequest = Assert.Single(handler.BoardsQueries);
        Assert.Contains("$expand=relations", boardsRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("fields=", boardsRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownWorkItemMustNotBeQueriedFromBoards()
    {
        var handler = new DeliveryHandler();
        using var service = CreateService(handler);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetStatusAsync(999_999, CancellationToken.None));
        Assert.Equal(0, handler.BoardsRequestCount);
    }

    private static DeliveryStatusService CreateService(
        DeliveryHandler handler,
        bool configuredBoards = true,
        Func<HttpRequestMessage, HttpResponseMessage>? boardsResponse = null,
        Func<HttpRequestMessage, HttpResponseMessage>? githubResponse = null,
        TimeProvider? timeProvider = null,
        string boardsAuthMode = "ManagedIdentity",
        string? boardsPat = null,
        string? githubToken = null)
    {
        if (boardsResponse is not null || githubResponse is not null)
        {
            handler = new DeliveryHandler(boardsResponse, githubResponse);
        }

        var github = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
        var boards = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://dev.azure.com/ai-enabled-ado-org/") };
        var settings = new Dictionary<string, string?>
        {
            ["BoardsStatus:Organization"] = configuredBoards ? "ai-enabled-ado-org" : null,
            ["BoardsStatus:Project"] = configuredBoards ? "sample-project" : null,
            ["BoardsStatus:AuthMode"] = configuredBoards ? boardsAuthMode : null,
            ["BoardsStatus:Pat"] = boardsPat,
            ["GitHubStatus:Token"] = githubToken
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new DeliveryStatusService(github, boards, configuration, new FakeCredential(), timeProvider ?? TimeProvider.System);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal("499b84ac-1321-427f-aa17-267ca6975798/.default", Assert.Single(requestContext.Scopes));
            return new(FakeAccessToken, DateTimeOffset.UtcNow.AddMinutes(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            Assert.Equal("499b84ac-1321-427f-aa17-267ca6975798/.default", Assert.Single(requestContext.Scopes));
            return ValueTask.FromResult(new AccessToken(FakeAccessToken, DateTimeOffset.UtcNow.AddMinutes(1)));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse(
            "2026-10-13T10:00:00Z",
            global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class DeliveryHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? boardsResponse = null,
        Func<HttpRequestMessage, HttpResponseMessage>? githubResponse = null) : HttpMessageHandler
    {
        public int BoardsRequestCount { get; private set; }
        public int GitHubRequestCount { get; private set; }
        public int ConditionalGitHubRequestCount { get; private set; }
        public string? LastBoardsAuthorization { get; private set; }
        public string? LastGitHubAuthorization { get; private set; }
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> BoardsQueries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path));
            if (request.RequestUri.Host == "dev.azure.com")
            {
                BoardsRequestCount++;
                BoardsQueries.Add(request.RequestUri.Query);
                LastBoardsAuthorization = request.Headers.Authorization?.ToString();
                return Task.FromResult(boardsResponse?.Invoke(request) ?? BoardsItemResponse());
            }

            GitHubRequestCount++;
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                ConditionalGitHubRequestCount++;
            }

            LastGitHubAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult((githubResponse ?? StandardGitHubResponse)(request));
        }

        public static HttpResponseMessage StandardGitHubResponse(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/issues", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, $$"""
                    [{
                      "number": 42,
                      "title": "[AB#{{WorkItemId}}] show delivery progress",
                      "body": "Azure Boards work item: [AB#{{WorkItemId}}](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/{{WorkItemId}})",
                      "author_association": "OWNER",
                      "html_url": "https://github.com/cihanduruer/agentic-development/issues/42",
                      "updated_at": "2026-10-13T10:00:00Z"
                    }, {
                      "number": 45,
                      "title": "[AB#966] another delivery",
                      "body": "Azure Boards work item: [AB#966](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/966)",
                      "author_association": "OWNER",
                      "html_url": "https://github.com/cihanduruer/agentic-development/issues/45",
                      "updated_at": "2026-10-13T10:05:00Z"
                    }]
                    """);
            }

            if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, $$"""
                    [{
                      "number": 43,
                      "title": "AB#{{WorkItemId}}: delivery status",
                      "body": "AB#{{WorkItemId}}",
                      "html_url": "https://github.com/cihanduruer/agentic-development/pull/43",
                      "updated_at": "2026-10-13T10:10:00Z",
                      "state": "open",
                      "merged_at": null,
                      "base": { "ref": "main" },
                      "head": { "sha": "{{HeadSha}}", "repo": { "full_name": "cihanduruer/agentic-development" } }
                    }, {
                      "number": 44,
                      "title": "AB#966: completed delivery",
                      "body": "AB#966",
                      "html_url": "https://github.com/cihanduruer/agentic-development/pull/44",
                      "updated_at": "2026-10-13T10:15:00Z",
                      "state": "closed",
                      "merged_at": "2026-10-13T10:14:00Z",
                      "merge_commit_sha": "fedcba9876543210fedcba9876543210fedcba98",
                      "base": { "ref": "main" },
                      "head": { "sha": "abcdef0123456789abcdef0123456789abcdef01", "repo": { "full_name": "cihanduruer/agentic-development" } }
                    }]
                    """);
            }

            return JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "workflow_runs": [{
                    "name": "PR validation",
                    "workflow_name": "PR validation",
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/123",
                    "created_at": "2026-10-13T10:00:00Z",
                    "run_started_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:30Z",
                    "pull_requests": [{ "number": 43 }]
                  }, {
                    "name": "Agentic intake",
                    "status": "completed",
                    "conclusion": "success",
                    "display_title": "AB#{{WorkItemId}} started",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/124",
                    "created_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:10Z",
                    "pull_requests": []
                  }, {
                    "name": "Hotel code review",
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/126",
                    "created_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:10Z",
                    "pull_requests": [{ "number": 43 }]
                  }, {
                    "name": "Hotel browser evidence",
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/127",
                    "created_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:10Z",
                    "pull_requests": [{ "number": 43 }]
                  }, {
                    "name": "QA evidence",
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/128",
                    "created_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:10Z",
                    "pull_requests": [{ "number": 43 }]
                  }, {
                    "name": "Deploy development",
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/129",
                    "created_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:10Z",
                    "pull_requests": [{ "number": 43 }]
                  }, {
                    "name": "Sync development lifecycle",
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/130",
                    "created_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:10Z",
                    "pull_requests": [{ "number": 43 }]
                  }, {
                    "name": "Release proposal",
                    "status": "completed",
                    "conclusion": "success",
                    "head_sha": "{{HeadSha}}",
                    "html_url": "https://github.com/cihanduruer/agentic-development/actions/runs/131",
                    "created_at": "2026-10-13T10:00:00Z",
                    "updated_at": "2026-10-13T10:00:10Z",
                    "pull_requests": [{ "number": 43 }]
                  }]
                }
                """);
        }

        private static HttpResponseMessage BoardsItemResponse() => JsonResponse(HttpStatusCode.OK, $$"""
            {
              "id": {{WorkItemId}},
              "fields": {
                "System.Id": {{WorkItemId}},
                "System.Title": "Delivery status",
                "System.State": "Active",
                "System.WorkItemType": "User Story",
                "System.Tags": "delivery-evidence; github-synced",
                "System.ChangedDate": "2026-10-13T10:20:00Z",
                "System.Description": "secret-body-must-not-leak",
                "System.AssignedTo": { "displayName": "Private person" }
              },
              "relations": [
                {
                  "rel": "ArtifactLink",
                  "url": "https://github.com/cihanduruer/agentic-development/pull/43",
                  "attributes": { "name": "Pull Request" }
                },
                {
                  "rel": "Hyperlink",
                  "url": "https://attacker.example/steal",
                  "attributes": { "name": "Malicious link" }
                }
              ]
            }
            """);
    }
}
