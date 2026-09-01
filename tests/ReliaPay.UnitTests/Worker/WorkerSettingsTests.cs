using FluentAssertions;
using ReliaPay.Worker;

namespace ReliaPay.UnitTests.Worker;

public sealed class WorkerSettingsTests
{
    [Fact]
    public void Test_profile_polls_at_most_100ms_and_uses_2000ms_lease()
    {
        WorkerSettings settings = WorkerSettings.Create("worker-a", "1", "after_job_claim");
        settings.InstanceId.Should().Be("worker-a");
        settings.PollInterval.Should().Be(TimeSpan.FromMilliseconds(100));
        settings.LeaseMs.Should().Be(2000);
        settings.Failpoint.Should().Be("after_job_claim");
    }

    [Fact]
    public void Missing_instance_id_is_rejected()
    {
        FluentActions.Invoking(() => WorkerSettings.Create("  ", null, null))
            .Should()
            .Throw<InvalidOperationException>();
    }
}
