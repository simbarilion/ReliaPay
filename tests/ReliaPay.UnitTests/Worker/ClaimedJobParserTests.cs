using System.Text.Json;
using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Worker;

namespace ReliaPay.UnitTests.Worker;

public sealed class ClaimedJobParserTests
{
    [Fact]
    public void Empty_jobs_array_is_idle()
    {
        ClaimParseResult parsed = ClaimedJobParser.Parse("""{"status":"ok","jobs":[]}""");
        parsed.Jobs.Should().BeEmpty();
        parsed.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Claimed_job_builds_manifest_and_trusted_context_from_payload()
    {
        const string json = """
            {
              "status": "ok",
              "jobs": [
                {
                  "jobId": "11111111-1111-1111-1111-111111111111",
                  "processId": "22222222-2222-2222-2222-222222222222",
                  "stepInstanceId": "33333333-3333-3333-3333-333333333333",
                  "executionId": "44444444-4444-4444-4444-444444444444",
                  "attemptId": "66666666-6666-6666-6666-666666666666",
                  "leaseOwner": "worker-a",
                  "leaseVersion": 2,
                  "leaseUntil": "2026-09-01T12:00:00+00:00",
                  "module": "training",
                  "action": "canary",
                  "actionVersion": 1,
                  "requestSchema": {"type":"object"},
                  "responseSchema": {"type":"object"},
                  "outcomes": ["APPLIED"],
                  "requiredPolicy": ["workflow:execute"],
                  "timeoutMs": 1600,
                  "inputMapping": {"/value":"/subject"},
                  "inputConstants": {"marker":"x"},
                  "processData": {"subject":"hello"}
                }
              ]
            }
            """;
        ClaimParseResult parsed = ClaimedJobParser.Parse(json);
        ClaimedJob job = parsed.Jobs.Should().ContainSingle().Subject;
        job.LeaseOwner.Should().Be("worker-a");
        job.LeaseVersion.Should().Be(2);
        job.TimeoutMs.Should().Be(1600);
        job.InputMapping["/value"].Should().Be("/subject");
        ActionManifest manifest = job.ToManifest();
        manifest.Module.Should().Be("training");
        manifest.Action.Should().Be("canary");
        manifest.Version.Should().Be(1);
        manifest.Outcomes.Should().Equal("APPLIED");
        ReliaPay.Security.TrustedContext context = job.ToTrustedContext(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        context.Principal.Should().Be("workflow-worker");
        context.Consumer.Should().Be("internal");
        context.RequestId.Should().Be("44444444-4444-4444-4444-444444444444");
        context.ExecutionId.Should().Be("44444444-4444-4444-4444-444444444444");
        context.ProcessId.Should().Be("22222222-2222-2222-2222-222222222222");
        context.JobId.Should().Be("11111111-1111-1111-1111-111111111111");
        context.AttemptId.Should().Be("66666666-6666-6666-6666-666666666666");
        context.Scopes.Should().Equal("workflow:execute");
        context.Deadline.Should().Be(DateTimeOffset.Parse("2026-09-01T12:00:00Z").AddMilliseconds(1600));
    }
}
