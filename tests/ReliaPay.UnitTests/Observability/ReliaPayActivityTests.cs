using System.Diagnostics;
using FluentAssertions;
using ReliaPay.Observability;

namespace ReliaPay.UnitTests.Observability;

public sealed class ReliaPayActivityTests
{
    [Fact]
    public void ApplyTags_sets_route_module_action_and_correlation()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name == ReliaPayInstrumentation.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using Activity activity = ReliaPayInstrumentation.ActivitySource.StartActivity("test")!;
        RequestTrace trace = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "key-1",
            "/api/payment/request",
            "payment",
            "request");

        ReliaPayActivity.ApplyTags(activity, trace);

        activity.GetTagItem("http.route").Should().Be("/api/payment/request");
        activity.GetTagItem("module").Should().Be("payment");
        activity.GetTagItem("action").Should().Be("request");
        activity.GetTagItem("correlation.id").Should().Be("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        activity.GetTagItem("request.id").Should().Be("key-1");
        activity.TagObjects.Should().NotContain(tag => string.Equals(tag.Key, "payload", StringComparison.OrdinalIgnoreCase));
        activity.TagObjects.Should().NotContain(tag => tag.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }
}
