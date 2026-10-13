using DevFlow.Core;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public class DomainTests
{
    [Fact] public void New_task_requires_plan_before_queueing()
    {
        var task = WorkTask.Create(Guid.NewGuid(), " API ", " Build API ", " Tests pass ");
        Assert.Equal(Status.AwaitingPlan, task.Status);
        Assert.Equal("API", task.Title);
        Assert.Throws<ConflictException>(() => task.ApprovePlan());
    }
    [Theory]
    [InlineData("", "description", "criteria")]
    [InlineData("title", " ", "criteria")]
    [InlineData("title", "description", "")]
    public void Blank_requirements_rejected(string title, string description, string criteria) =>
        Assert.Throws<ArgumentException>(() => WorkTask.Create(Guid.NewGuid(), title, description, criteria));
    [Fact] public void Approving_plan_queues_task_and_increments_version()
    {
        var task = WorkTask.Create(Guid.NewGuid(), "API", "Build", "Tests").SetPlan("Write tests then implement");
        Assert.Equal(Status.AwaitingApproval, task.Status);
        var queued = task.ApprovePlan();
        Assert.Equal(Status.Queued, queued.Status);
        Assert.Equal(2, queued.Version);
        Assert.Throws<ConflictException>(() => queued.ApprovePlan());
    }
    [Fact] public void Cancel_is_idempotent_and_completed_work_cannot_be_cancelled()
    {
        var task = WorkTask.Create(Guid.NewGuid(), "API", "Build", "Tests");
        var cancelled = task.Cancel();
        Assert.Equal(Status.Cancelled, cancelled.Status);
        Assert.Equal(cancelled, cancelled.Cancel());
        Assert.Throws<ConflictException>(() => (task with { Status = Status.AwaitingReview }).Cancel());
    }
}
