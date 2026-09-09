using FluentAssertions;
using ReliaPay.Contracts;

namespace ReliaPay.UnitTests.Data;

public sealed class WorkflowManualBoundaryTests
{
    private const string Migration = "20260909040000_inbox_reconcile_and_manual.sql";

    [Fact]
    public void Migration_publishes_workflow_manual_with_audit_and_conflict()
    {
        string sql = ReadRepoFile("db", "migrations", Migration);
        sql.Should().Contain("CREATE FUNCTION app.workflow_manual(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("workflow.decision_conflict");
        sql.Should().Contain("'MANUAL'");
        sql.Should().Contain("reason_hash");
        sql.Should().Contain("p_context ->> 'principal'");
        sql.Should().Contain("WAITING_MANUAL");
        sql.Should().Contain("\"action\": \"manual\"");
        sql.Should().Contain("\"required_policy\": [\"workflow:manual\"]");
        sql.Should().Contain("api.activate_action('workflow.manual', 1)");
        sql.Should().Contain("REVOKE ALL ON FUNCTION app.workflow_manual(jsonb, jsonb) FROM PUBLIC");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION app.workflow_manual(jsonb, jsonb) TO inbox_reconciler");
        sql.Should().Contain("decisions_manual_once");
    }

    [Fact]
    public void Bundled_manifest_and_schema_match_manual_contract()
    {
        string manifest = ReadRepoFile("db", "manifests", "workflow.manual.v1.json");
        manifest.Should().Contain("\"module\": \"workflow\"");
        manifest.Should().Contain("\"action\": \"manual\"");
        manifest.Should().Contain("\"target_function\": \"workflow_manual\"");
        manifest.Should().Contain("\"workflow:manual\"");
        manifest.Should().Contain("\"idempotency_mode\": \"required\"");
        manifest.Should().Contain("processId");
        manifest.Should().Contain("stepInstanceId");
        manifest.Should().Contain("APPROVED");
        manifest.Should().Contain("REJECTED");

        string payload = ReadRepoFile("contracts", "course-1", "workflow-manual.payload.schema.json");
        payload.Should().Contain("urn:course:course-1:workflow-manual-payload");
        payload.Should().Contain("\"maxLength\": 500");

        string result = ReadRepoFile("contracts", "course-1", "workflow-manual.result.schema.json");
        result.Should().Contain("urn:course:course-1:workflow-manual-result");
        result.Should().Contain("\"const\": \"MANUAL\"");
        result.Should().Contain("decisionId");
    }

    [Fact]
    public void Decision_conflict_is_http_409()
    {
        ErrorCodes.WorkflowDecisionConflict.Should().Be("workflow.decision_conflict");
        HttpErrorMatrix.StatusCode(ErrorCodes.WorkflowDecisionConflict).Should().Be(409);
        HttpErrorMatrix.Retryable(ErrorCodes.WorkflowDecisionConflict).Should().BeFalse();
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
