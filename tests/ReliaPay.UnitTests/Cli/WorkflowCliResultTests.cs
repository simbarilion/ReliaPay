using System.Text.Json.Nodes;
using FluentAssertions;
using ReliaPay.Cli;

namespace ReliaPay.UnitTests.Cli;

public sealed class WorkflowCliResultTests
{
    [Fact]
    public void Start_result_gets_operation_started_without_removing_process_id()
    {
        string json = WorkflowCliResult.ApplyContractFields(
            """{"status":"ok","result":{"resource":"process","processId":"11111111-1111-1111-1111-111111111111","flowName":"demo","flowVersion":1,"state":"RUNNING"}}""",
            extra: new Dictionary<string, string> { ["operation"] = "started" });

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        JsonObject result = root["result"]!.AsObject();
        result["operation"]!.GetValue<string>().Should().Be("started");
        result["processId"]!.GetValue<string>().Should().Be("11111111-1111-1111-1111-111111111111");
        result["resource"]!.GetValue<string>().Should().Be("process");
    }

    [Fact]
    public void Publish_result_keeps_version_and_adds_flow_version()
    {
        string json = WorkflowCliResult.ApplyContractFields(
            """{"status":"ok","resource":"flow","operation":"published","flowName":"demo","version":2}""");

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        root["version"]!.GetValue<int>().Should().Be(2);
        root["flowVersion"]!.GetValue<int>().Should().Be(2);
        root["operation"]!.GetValue<string>().Should().Be("published");
    }

    [Fact]
    public void Signal_result_gets_process_id_and_type()
    {
        string json = WorkflowCliResult.ApplyContractFields(
            """{"status":"ok","result":{"resource":"signal","status":"accepted","messageId":"msg-1"}}""",
            extra: new Dictionary<string, string>
            {
                ["processId"] = "22222222-2222-2222-2222-222222222222",
                ["signalType"] = "training.completed",
            });

        JsonObject result = JsonNode.Parse(json)!["result"]!.AsObject();
        result["status"]!.GetValue<string>().Should().Be("accepted");
        result["messageId"]!.GetValue<string>().Should().Be("msg-1");
        result["processId"]!.GetValue<string>().Should().Be("22222222-2222-2222-2222-222222222222");
        result["signalType"]!.GetValue<string>().Should().Be("training.completed");
    }
}
