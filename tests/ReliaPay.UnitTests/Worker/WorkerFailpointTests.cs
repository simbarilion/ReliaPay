using FluentAssertions;
using ReliaPay.Worker;

namespace ReliaPay.UnitTests.Worker;

public sealed class WorkerFailpointTests
{
    [Fact]
    public async Task Matching_failpoint_writes_ack_json_then_waits()
    {
        using StringWriter output = new();
        WorkerFailpoint gate = new("after_job_claim", output);
        using CancellationTokenSource cancel = new();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        await gate.Invoking(item => item.HitAsync("after_job_claim", "worker-a", cancel.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        output.ToString().Should().Contain("\"event\":\"failpoint.reached\"");
        output.ToString().Should().Contain("\"name\":\"after_job_claim\"");
        output.ToString().Should().Contain("\"instanceId\":\"worker-a\"");
    }

    [Fact]
    public async Task Other_failpoint_name_is_ignored()
    {
        using StringWriter output = new();
        WorkerFailpoint gate = new("after_action_before_finish", output);
        await gate.HitAsync("after_job_claim", "worker-a", CancellationToken.None);
        output.ToString().Should().BeEmpty();
    }
}
