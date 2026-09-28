using FluentAssertions;
using ReliaPay.Execution;

namespace ReliaPay.UnitTests.Execution;

public sealed class CourseFailpointTests
{
    [Fact]
    public async Task Matching_failpoint_in_test_profile_writes_ack_then_waits()
    {
        using StringWriter output = new();
        CourseFailpoint gate = new("after_inbox_saved", testProfileEnabled: true, instanceId: "api", output);
        using CancellationTokenSource cancel = new();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        await gate.Invoking(item => item.HitAsync("after_inbox_saved", cancel.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        output.ToString().Should().Contain("\"event\":\"failpoint.reached\"");
        output.ToString().Should().Contain("\"name\":\"after_inbox_saved\"");
        output.ToString().Should().Contain("\"instanceId\":\"api\"");
    }

    [Fact]
    public async Task Production_profile_never_arms_even_if_name_matches()
    {
        using StringWriter output = new();
        CourseFailpoint gate = new("after_manual_decision", testProfileEnabled: false, instanceId: "api", output);
        await gate.HitAsync("after_manual_decision", CancellationToken.None);
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Other_failpoint_name_is_ignored()
    {
        using StringWriter output = new();
        CourseFailpoint gate = new("after_manual_decision", testProfileEnabled: true, instanceId: "api", output);
        await gate.HitAsync("after_inbox_saved", CancellationToken.None);
        output.ToString().Should().BeEmpty();
    }
}