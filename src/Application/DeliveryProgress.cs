namespace AgenticHotelBooking.Application;

public sealed record DeliveryProgressStep(string Name, string Status, string? Url);

public static class DeliveryProgress
{
    public static IReadOnlyList<DeliveryProgressStep> Build(
        DeliverySummary delivery, DevelopmentApprovalState? approval, bool stale)
    {
        DeliveryStageSummary? Stage(string name) => delivery.Stages.FirstOrDefault(stage => stage.Name == name);
        var intake = Stage("Boards intake");
        var development = Stage("GitHub issue and cloud agent");
        var checks = new[] { Stage("PR validation"), Stage("Copilot review gate"), Stage("QA evidence") };
        var deployment = Stage("Development deployment");
        var verification = Stage("Lifecycle sync and delivery evidence");
        var merged = Stage("Merge")?.Status == "Passed";
        var verified = approval?.PullRequestNumber == delivery.PullRequestNumber &&
            approval?.HeadSha == Stage("Pull request")?.HeadSha &&
            (approval?.CanApprove == true || approval?.Status == "Publishing");
        var approved = approval?.Status == "Publishing" && merged &&
            verified;
        var checkStatus = Combine(checks);
        var steps = new[]
        {
            new DeliveryProgressStep("Received", intake?.Status ?? "Not started", intake?.Url ?? delivery.GitHubIssueUrl),
            new DeliveryProgressStep("Being built",
                delivery.IsDraft && development?.Status == "Passed" ? "Waiting" : development?.Status ?? "Not started",
                development?.Url ?? delivery.PullRequestUrl),
            new DeliveryProgressStep("Checking", verified ? "Passed" : checkStatus == "Passed" ? "Waiting" : checkStatus,
                checks.FirstOrDefault(stage => stage?.Status != "Passed")?.Url ?? checks[0]?.Url),
            new DeliveryProgressStep("Your approval", approved ? "Passed" : verified && approval?.CanApprove == true ? "Waiting" : "Not started",
                approval?.ApprovalUrl ?? delivery.PullRequestUrl),
            new DeliveryProgressStep("Publishing", approved ? deployment?.Status ?? "Not started" : "Not started", deployment?.Url),
            new DeliveryProgressStep("Ready", approved && deployment?.Status == "Passed" ? verification?.Status ?? "Not started" : "Not started", verification?.Url)
        };

        var blocked = false;
        return steps.Select(step =>
        {
            var status = blocked ? "Not started" : stale ? "Stale" : step.Status;
            if (status != "Passed")
            {
                blocked = true;
            }
            return step with { Status = status };
        }).ToArray();
    }

    private static string Combine(IEnumerable<DeliveryStageSummary?> stages)
    {
        var statuses = stages.Select(stage => stage?.Status ?? "Not started").ToArray();
        return statuses.Contains("Failed") ? "Failed" :
            statuses.Contains("Running") ? "Running" :
            statuses.All(status => status == "Passed") ? "Passed" :
            statuses.Contains("Skipped") ? "Skipped" : "Waiting";
    }
}
