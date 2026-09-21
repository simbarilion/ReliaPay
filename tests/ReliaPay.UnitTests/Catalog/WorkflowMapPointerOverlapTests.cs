using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Json;

namespace ReliaPay.UnitTests.Catalog;

public sealed class WorkflowMapPointerOverlapTests
{
    private static readonly WorkflowMapReader Reader = new(new JsonDocumentValidator());
    private static readonly WorkflowMapSemanticValidator Semantics = new();

    private static readonly StaticPublishedActionLookup Catalog = new(
        new PublishedActionSnapshot(
            "training",
            "canary",
            1,
            Enabled: true,
            Outcomes: ["APPLIED"],
            RequiredPolicy: ["workflow:execute"]));

    [Fact]
    public async Task Constant_slash_name_conflicts_with_escaped_mapping_target()
    {
        WorkflowMap map = ReadMap(
            mapping: """{ "/a~1b": "/value" }""",
            constants: """{ "a/b": "x" }""");
        (string Code, string Message)? error = await Semantics.ValidateAsync(map, Catalog, CancellationToken.None);
        error.Should().NotBeNull();
        error!.Value.Message.Should().Contain("overlap");
    }

    [Fact]
    public async Task Constant_slash_name_does_not_conflict_with_parent_mapping_target()
    {
        WorkflowMap map = ReadMap(
            mapping: """{ "/foo": "/value" }""",
            constants: """{ "foo/bar": "x" }""");
        (await Semantics.ValidateAsync(map, Catalog, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Constant_tilde_name_conflicts_with_escaped_mapping_target()
    {
        WorkflowMap map = ReadMap(
            mapping: """{ "/a~0b": "/value" }""",
            constants: """{ "a~b": "x" }""");
        (string Code, string Message)? error = await Semantics.ValidateAsync(map, Catalog, CancellationToken.None);
        error.Should().NotBeNull();
        error!.Value.Message.Should().Contain("overlap");
    }

    [Fact]
    public async Task Neighbor_tilde_constant_does_not_conflict_with_parent_mapping()
    {
        WorkflowMap map = ReadMap(
            mapping: """{ "/foo": "/value" }""",
            constants: """{ "foo~1bar": "x" }""");
        (await Semantics.ValidateAsync(map, Catalog, CancellationToken.None)).Should().BeNull();
    }

    private static WorkflowMap ReadMap(string mapping, string constants)
    {
        string json = $$"""
            {
              "contract_version": "course-1",
              "flow_name": "workflow-smoke",
              "version": 1,
              "start_step": "invoke_canary",
              "steps": [
                {
                  "key": "invoke_canary",
                  "type": "automatic",
                  "task": {
                    "service": "postgres",
                    "module": "training",
                    "action": "canary",
                    "action_version": 1,
                    "required_policy": ["workflow:execute"],
                    "timeout_ms": 2000,
                    "retry": { "max_attempts": 3, "delays_ms": [200, 400] },
                    "input_mapping": {{mapping}},
                    "input_constants": {{constants}}
                  }
                },
                {
                  "key": "wait_result",
                  "type": "wait_signal",
                  "signal_type": "training.completed",
                  "outcome": "RECEIVED"
                },
                {
                  "key": "done",
                  "type": "end",
                  "outcome": "COMPLETED"
                }
              ],
              "transitions": [
                { "from": "invoke_canary", "outcome": "APPLIED", "to": "wait_result" },
                { "from": "wait_result", "outcome": "RECEIVED", "to": "done" }
              ]
            }
            """;
        WorkflowMapReadResult result = Reader.Read(json);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Map!;
    }
}
