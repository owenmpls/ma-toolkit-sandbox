using FluentAssertions;
using MaToolkit.Automation.Shared.Constants;
using Xunit;

namespace MaToolkit.Automation.Shared.Tests.Constants;

public class StatusConstantsTests
{
    #region BatchStatus Tests

    [Fact]
    public void BatchStatus_HasCorrectValues()
    {
        BatchStatus.Detected.Should().Be("detected");
        BatchStatus.InitDispatched.Should().Be("init_dispatched");
        BatchStatus.Active.Should().Be("active");
        BatchStatus.Completed.Should().Be("completed");
        BatchStatus.Failed.Should().Be("failed");
    }

    [Fact]
    public void BatchStatus_ValuesAreSnakeCase()
    {
        var allValues = new[]
        {
            BatchStatus.Detected,
            BatchStatus.InitDispatched,
            BatchStatus.Active,
            BatchStatus.Completed,
            BatchStatus.Failed
        };

        allValues.Should().OnlyContain(v => v == v.ToLowerInvariant());
        allValues.Should().OnlyContain(v => !v.Contains(' '));
    }

    #endregion

    #region PhaseStatus Tests

    [Fact]
    public void PhaseStatus_HasCorrectValues()
    {
        PhaseStatus.Pending.Should().Be("pending");
        PhaseStatus.Dispatched.Should().Be("dispatched");
        PhaseStatus.Completed.Should().Be("completed");
        PhaseStatus.Skipped.Should().Be("skipped");
        PhaseStatus.Failed.Should().Be("failed");
    }

    #endregion

    #region StepStatus Tests

    [Fact]
    public void StepStatus_HasCorrectValues()
    {
        StepStatus.Pending.Should().Be("pending");
        StepStatus.Dispatched.Should().Be("dispatched");
        StepStatus.Succeeded.Should().Be("succeeded");
        StepStatus.Failed.Should().Be("failed");
        StepStatus.Polling.Should().Be("polling");
        StepStatus.PollTimeout.Should().Be("poll_timeout");
        StepStatus.Cancelled.Should().Be("cancelled");
    }

    [Fact]
    public void StepStatus_PollingRelatedStatusesExist()
    {
        // Ensure polling-related statuses are defined
        StepStatus.Polling.Should().NotBeNullOrEmpty();
        StepStatus.PollTimeout.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region MemberStatus Tests

    [Fact]
    public void MemberStatus_HasCorrectValues()
    {
        MemberStatus.Active.Should().Be("active");
        MemberStatus.Removed.Should().Be("removed");
    }

    #endregion

    #region Cross-Status Consistency Tests

    [Fact]
    public void AllStatusConstants_UseConsistentNaming()
    {
        // All failed states should use the same string
        BatchStatus.Failed.Should().Be("failed");
        PhaseStatus.Failed.Should().Be("failed");
        StepStatus.Failed.Should().Be("failed");
    }

    [Fact]
    public void AllStatusConstants_UseConsistentCompletedNaming()
    {
        BatchStatus.Completed.Should().Be("completed");
        PhaseStatus.Completed.Should().Be("completed");
        // Note: StepStatus uses "succeeded" for completed steps, not "completed"
        StepStatus.Succeeded.Should().Be("succeeded");
    }

    #endregion
}
