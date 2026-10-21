using AgenticHotelBooking.Application;

namespace AgenticHotelBooking.UnitTests;

public sealed class DeliveryProgressTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string[] StageNames =
    [
        "Boards intake", "GitHub issue and cloud agent", "Pull request", "PR validation", "Copilot review gate",
        "QA evidence", "Merge", "Development deployment", "Lifecycle sync and delivery evidence"
    ];

    [Fact]
    public void StepsHaveOneLinearProductOwnerOrder()
    {
        var progress = DeliveryProgress.Build(Delivery(), null, false);
        Assert.Equal(["Received", "Being built", "Checking", "Your approval", "Publishing", "Ready"], progress.Select(step => step.Name));
        Assert.Equal(["Passed", "Passed", "Waiting", "Not started", "Not started", "Not started"], progress.Select(step => step.Status));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Failed")]
    [InlineData("Skipped")]
    [InlineData("Not started")]
    public void RequiredCheckCannotBeGreenAndFutureStepsRemainUpcoming(string status)
    {
        var delivery = Delivery();
        delivery = delivery with { Stages = delivery.Stages.Select(stage => stage.Name == "QA evidence" ? stage with { Status = status } : stage).ToArray() };
        var progress = DeliveryProgress.Build(delivery, null, false);
        Assert.NotEqual("Passed", progress[2].Status);
        Assert.All(progress.Skip(3), step => Assert.Equal("Not started", step.Status));
        Assert.True(progress.Count(step => step.Status == "Running") <= 1);
    }

    [Fact]
    public void AllGreenRequiresApprovalMergeDeploymentAndVerificationNotProduction()
    {
        var progress = DeliveryProgress.Build(Delivery(), Approval(), false);
        Assert.All(progress, step => Assert.Equal("Passed", step.Status));
        foreach (var name in new[] { "Merge", "Development deployment", "Lifecycle sync and delivery evidence" })
        {
            var delivery = Delivery();
            delivery = delivery with { Stages = delivery.Stages.Select(stage => stage.Name == name ? stage with { Status = "Waiting" } : stage).ToArray() };
            Assert.Contains(DeliveryProgress.Build(delivery, Approval(), false), step => step.Status != "Passed");
        }
    }

    [Fact]
    public void StaleEvidenceOrWrongRevisionOrDraftCannotProduceAllGreen()
    {
        Assert.DoesNotContain(DeliveryProgress.Build(Delivery(), Approval(), true), step => step.Status == "Passed");
        Assert.NotEqual("Passed", DeliveryProgress.Build(Delivery(), Approval() with { HeadSha = new string('a', 40) }, false)[3].Status);
        Assert.NotEqual("Passed", DeliveryProgress.Build(Delivery() with { IsDraft = true }, Approval(), false)[1].Status);
    }

    [Fact]
    public void ApprovalIsOnlyCurrentActionAfterChecks()
    {
        var progress = DeliveryProgress.Build(Delivery(), Approval() with { Status = "Ready", CanApprove = true }, false);
        Assert.Equal("Your approval", progress.First(step => step.Status != "Passed").Name);
        Assert.Equal("Waiting", progress[3].Status);
        Assert.All(progress.Skip(4), step => Assert.Equal("Not started", step.Status));
    }

    [Fact]
    public void TrustedMainPolicyReadinessCanCompleteCheckingWhenRecentPrListCannotAttributeQaRun()
    {
        var delivery = Delivery();
        delivery = delivery with { Stages = delivery.Stages.Select(stage => stage.Name == "QA evidence" ? stage with { Status = "Waiting" } : stage).ToArray() };
        var approval = Approval() with { Status = "Ready", CanApprove = true };
        var progress = DeliveryProgress.Build(delivery, approval, false);
        Assert.Equal("Passed", progress[2].Status);
        Assert.Equal("Your approval", progress.First(step => step.Status != "Passed").Name);
        Assert.NotEqual("Passed", DeliveryProgress.Build(delivery, approval with { HeadSha = new string('a', 40) }, false)[2].Status);
    }

    private static DevelopmentApprovalState Approval() => new(77, Sha, false, "Publishing", "Approved");
    private static DeliverySummary Delivery() => new(975, "Example request", "https://github.com/cihanduruer/agentic-development/issues/76",
        76, DateTimeOffset.UtcNow, "Waiting", "Example change", 77, "https://github.com/cihanduruer/agentic-development/pull/77",
        StageNames.Select(name => new DeliveryStageSummary(name, "Passed", null, null, "https://github.com/cihanduruer/agentic-development/actions/runs/1", Sha))
            .Append(new("Human production approval", "Waiting", null, null, null, null)).ToArray(), null);
}
