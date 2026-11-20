using Pulaski.Application.Execution;

namespace Pulaski.Application.Tests.Execution;

/// <summary>What a line display board says of a line at a glance (#120).</summary>
public class LineDisplayStatusTests
{
    [Theory]
    [InlineData(true, "producing", true, "running")]
    [InlineData(true, "stopped_faulted", true, "down")]
    [InlineData(true, "held_internal", false, "down")]
    [InlineData(true, "waiting_external", true, "waiting")]
    [InlineData(true, "no_signal", true, "no_signal")]
    [InlineData(true, "ready_transitioning", true, "waiting")]
    [InlineData(true, "ready_transitioning", false, "idle")]
    [InlineData(false, null, true, "running")]
    [InlineData(false, null, false, "idle")]
    public void The_machine_state_decides_and_without_one_the_running_job_does(bool tracksState, string? category, bool hasJob, string status) =>
        Assert.Equal(status, LineDisplayStatus.Of(tracksState, category, hasJob));
}
