using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Json;

namespace ReliaPay.UnitTests.Catalog;

public sealed class WorkflowMapReaderTests
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
            RequiredPolicy: ["workflow:execute"]),
        new PublishedActionSnapshot(
            "training",
            "canary",
            2,
            Enabled: false,
            Outcomes: ["APPLIED"],
            RequiredPolicy: ["workflow:execute"]));

    [Fact]
    public void Valid_json_map_round_trips_flow_name()
    {
        WorkflowMapReadResult result = Reader.Read(ValidMap());
        result.IsSuccess.Should().BeTrue();
        result.Map!.FlowName.Should().Be("workflow-smoke");
        result.Map.Steps.Should().HaveCount(3);
    }

    [Fact]
    public void Yaml_map_is_accepted_as_json()
    {
        const string yaml = """
            contract_version: course-1
            flow_name: workflow-smoke
            version: 1
            start_step: invoke_canary
            steps:
              - key: invoke_canary
                type: automatic
                task:
                  service: postgres
                  module: training
                  action: canary
                  action_version: 1
                  required_policy:
                    - "workflow:execute"
                  timeout_ms: 2000
                  retry:
                    max_attempts: 3
                    delays_ms: [200, 400]
                  input_mapping:
                    "/value": "/value"
                  input_constants: {}
              - key: wait_result
                type: wait_signal
                signal_type: training.completed
                outcome: RECEIVED
              - key: done
                type: end
                outcome: COMPLETED
            transitions:
              - from: invoke_canary
                outcome: APPLIED
                to: wait_result
              - from: wait_result
                outcome: RECEIVED
                to: done
            """;
        ReliaPay.Json.YamlJsonConverter.TryConvert(yaml, out string json, out string? convertError);
        convertError.Should().BeNull();
        json.Should().Contain("\"version\":1");
        json.Should().NotContain("\"version\":\"1\"");
        WorkflowMapReadResult result = Reader.Read(yaml, "map.yaml");
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Map!.StartStep.Should().Be("invoke_canary");
        result.Map.Version.Should().Be(1);
    }

    [Fact]
    public void Quoted_yaml_keys_are_accepted()
    {
        const string yaml = """
            "contract_version": "course-1"
            "flow_name": "workflow-smoke"
            "version": 1
            "start_step": "invoke_canary"
            "steps":
              -
                "key": "invoke_canary"
                "type": "automatic"
                "task":
                  "service": "postgres"
                  "module": "training"
                  "action": "canary"
                  "action_version": 1
                  "required_policy":
                    - "workflow:execute"
                  "timeout_ms": 2000
                  "retry":
                    "max_attempts": 3
                    "delays_ms":
                      - 200
                      - 400
                  "input_mapping":
                    "/value": "/value"
                  "input_constants": {}
              -
                "key": "wait_result"
                "type": "wait_signal"
                "signal_type": "training.completed"
                "outcome": "RECEIVED"
              -
                "key": "done"
                "type": "end"
                "outcome": "COMPLETED"
            "transitions":
              -
                "from": "invoke_canary"
                "outcome": "APPLIED"
                "to": "wait_result"
              -
                "from": "wait_result"
                "outcome": "RECEIVED"
                "to": "done"
            """;
        WorkflowMapReadResult result = Reader.Read(yaml, "map.yaml");
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Map!.FlowName.Should().Be("workflow-smoke");
    }

    [Fact]
    public void Unknown_field_fails_schema()
    {
        Reader.Read(ValidMap().Replace("\"version\": 1", "\"version\": 1, \"checker_only\": true"))
            .ErrorCode.Should().Be(ErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Valid_map_passes_semantics()
    {
        WorkflowMap map = Reader.Read(ValidMap()).Map!;
        (await Semantics.ValidateAsync(map, Catalog, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Cycle_is_rejected()
    {
        string json = ValidMap().Replace(
            "\"to\": \"done\"",
            "\"to\": \"invoke_canary\"");
        WorkflowMap map = Reader.Read(json).Map!;
        (string Code, string Message)? error = await Semantics.ValidateAsync(map, Catalog, CancellationToken.None);
        error.Should().NotBeNull();
        error!.Value.Code.Should().Be(ErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Dead_end_wait_step_is_rejected()
    {
        string json = """
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
                    "input_mapping": { "/value": "/value" },
                    "input_constants": {}
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
                { "from": "invoke_canary", "outcome": "APPLIED", "to": "wait_result" }
              ]
            }
            """;
        WorkflowMap map = Reader.Read(json).Map!;
        (await Semantics.ValidateAsync(map, Catalog, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Disabled_action_version_is_rejected()
    {
        string json = ValidMap().Replace("\"action_version\": 1", "\"action_version\": 2");
        WorkflowMap map = Reader.Read(json).Map!;
        (string Code, string Message)? error = await Semantics.ValidateAsync(map, Catalog, CancellationToken.None);
        error.Should().NotBeNull();
        error!.Value.Message.Should().Contain("disabled");
    }

    [Fact]
    public async Task Overlapping_mapping_is_rejected()
    {
        string json = ValidMap().Replace(
            """{ "/value": "/value" }""",
            """{ "/value": "/value", "/value/nested": "/other" }""");
        WorkflowMap map = Reader.Read(json).Map!;
        (await Semantics.ValidateAsync(map, Catalog, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Unknown_action_is_rejected()
    {
        string json = ValidMap().Replace("\"action\": \"canary\"", "\"action\": \"missing\"");
        WorkflowMap map = Reader.Read(json).Map!;
        (string Code, string Message)? error = await Semantics.ValidateAsync(map, Catalog, CancellationToken.None);
        error.Should().NotBeNull();
        error!.Value.Message.Should().Contain("not published");
    }

    [Fact]
    public async Task Policy_mismatch_is_rejected()
    {
        string json = ValidMap().Replace("\"workflow:execute\"", "\"payment:write\"");
        WorkflowMap map = Reader.Read(json).Map!;
        (await Semantics.ValidateAsync(map, Catalog, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Bundled_manual_wait_map_passes_schema_and_semantics()
    {
        string json = ReadRepoFile("db", "maps", "workflow-manual.v1.json");
        WorkflowMapReadResult read = Reader.Read(json);
        read.IsSuccess.Should().BeTrue();
        read.Map!.FlowName.Should().Be("workflow-manual");
        read.Map.StartStep.Should().Be("review");
        read.Map.Steps.Should().Contain(step => step.Type == "manual");
        (await Semantics.ValidateAsync(read.Map, Catalog, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Retry_delay_length_must_match_max_attempts()
    {
        string json = ValidMap().Replace("\"delays_ms\": [200, 400]", "\"delays_ms\": [200]");
        WorkflowMap map = Reader.Read(json).Map!;
        (await Semantics.ValidateAsync(map, Catalog, CancellationToken.None)).Should().NotBeNull();
    }

    private static string ValidMap()
    {
        return """
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
                    "input_mapping": { "/value": "/value" },
                    "input_constants": {}
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
    }

    private static string ReadRepoFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Repository file {string.Join('/', parts)} was not found.");
    }
}

internal sealed class StaticPublishedActionLookup : IPublishedActionLookup
{
    private readonly IReadOnlyList<PublishedActionSnapshot> _actions;

    public StaticPublishedActionLookup(params PublishedActionSnapshot[] actions)
    {
        _actions = actions;
    }

    public Task<PublishedActionSnapshot?> FindAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        PublishedActionSnapshot? match = _actions.FirstOrDefault(
            item => item.Module == module && item.Action == action && item.Version == version);
        return Task.FromResult(match);
    }
}
