using FluentAssertions;
using ReliaPay.Contracts;
using ReliaPay.Data;

namespace ReliaPay.UnitTests.Data;

public sealed class WorkflowFunctionsTests
{
    [Fact]
    public void Worker_execute_boundary_is_claim_finish_and_fail()
    {
        WorkflowFunctions.WorkerExecute.Should().Equal(
            "claim_jobs",
            "finish_job",
            "fail_job");
        WorkflowFunctions.WorkerExecute.Should().NotContain(WorkflowFunctions.PublishFlow);
        WorkflowFunctions.WorkerExecute.Should().NotContain(WorkflowFunctions.StartProcess);
        WorkflowFunctions.WorkerExecute.Should().NotContain(WorkflowFunctions.SignalProcess);
    }

    [Fact]
    public void Workflow_migrations_declare_engine_functions_and_evidence_views()
    {
        string definitions = ReadMigration("20260826090000_workflow_definitions_and_runtime.sql");
        string functions = ReadMigration("20260826100000_workflow_functions.sql");
        string views = ReadMigration("20260826110000_workflow_views_and_privileges.sql");
        string startPayload = ReadMigration("20260826140000_process_start_payload.sql");

        definitions.Should().Contain("CREATE TABLE workflow.flow_versions");
        definitions.Should().Contain("CREATE TABLE workflow.processes");
        definitions.Should().Contain("CREATE TABLE workflow.jobs");
        definitions.Should().Contain("lease_version bigint");
        definitions.Should().Contain("CREATE TABLE workflow.attempts");
        definitions.Should().Contain("CREATE TABLE workflow.signals");
        definitions.Should().Contain("CREATE TABLE workflow.events");

        functions.Should().Contain("CREATE FUNCTION workflow.claim_jobs(p_owner text, p_lease_ms integer DEFAULT 2000)");
        functions.Should().Contain("CREATE FUNCTION workflow.finish_job(");
        functions.Should().Contain("CREATE FUNCTION workflow.fail_job(");
        functions.Should().Contain("CREATE FUNCTION workflow.publish_flow(p_document jsonb)");
        functions.Should().Contain("CREATE FUNCTION workflow.activate_flow(p_flow_name text, p_version integer)");
        functions.Should().Contain("CREATE FUNCTION workflow.start_process(");
        functions.Should().Contain("CREATE FUNCTION workflow.signal_process(");
        functions.Should().Contain("CREATE FUNCTION workflow.get_process(p_process_id uuid)");
        functions.Should().Contain("'TaskFailed'");
        functions.Should().Contain(ErrorCodes.WorkflowLeaseStale);
        functions.Should().Contain(ErrorCodes.WorkflowConflict);

        views.Should().Contain("CREATE VIEW autocheck.flow_versions");
        views.Should().Contain("CREATE VIEW autocheck.processes");
        views.Should().Contain("CREATE VIEW autocheck.steps");
        views.Should().Contain("CREATE VIEW autocheck.jobs");
        views.Should().Contain("CREATE VIEW autocheck.attempts");
        views.Should().Contain("CREATE VIEW autocheck.signals");
        views.Should().Contain("CREATE VIEW autocheck.workflow_events");

        views.Should().Contain("GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO workflow_worker");
        views.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text, integer) TO workflow_worker");
        views.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO workflow_worker");
        views.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean) TO workflow_worker");
        views.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO course_publication");

        startPayload.Should().Contain("ADD COLUMN IF NOT EXISTS start_payload jsonb");
        startPayload.Should().Contain("IF v_process.start_payload = v_data THEN");
        startPayload.Should().Contain("CREATE OR REPLACE FUNCTION workflow.start_process(");

        string contractFields = ReadMigration("20260826150000_workflow_contract_fields.sql");
        contractFields.Should().Contain("'attemptId', v_attempt_id");
        contractFields.Should().Contain("CREATE OR REPLACE FUNCTION workflow.claim_jobs(");
    }

    [Fact]
    public void Unknown_pinned_map_signal_type_is_rejected_before_insert()
    {
        string sql = ReadMigration("20260826160000_reject_unknown_signal_type.sql");

        sql.Should().Contain("CREATE OR REPLACE FUNCTION workflow.signal_process(");
        sql.Should().Contain("FROM workflow.step_definitions");
        sql.Should().Contain("AND flow_version = v_process.flow_version");
        sql.Should().Contain("AND step_type = 'WAIT_SIGNAL'");
        sql.Should().Contain("AND signal_type = p_type");
        sql.Should().Contain($"error_envelope('{ErrorCodes.RequestInvalid}'");
        sql.Should().Contain("INSERT INTO workflow.signals");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.signal_process(uuid, text, text, jsonb) TO course_publication");

        int typeCheck = sql.IndexOf("AND signal_type = p_type", StringComparison.Ordinal);
        int insert = sql.IndexOf("INSERT INTO workflow.signals", StringComparison.Ordinal);
        typeCheck.Should().BePositive();
        insert.Should().BeGreaterThan(typeCheck);
    }

    [Fact]
    public void Additive_hardening_keeps_pending_signal_fields_and_manual_map()
    {
        string sql = ReadMigration("20260826170000_keep_first_pending_signal_manual.sql");

        sql.Should().Contain("'PENDING'");
        sql.Should().Contain("step_instances_state_chk");
        sql.Should().Contain("'processId', p_process_id");
        sql.Should().Contain("'signalType', p_type");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION workflow.signal_process(");
        sql.Should().Contain("AND signal_type = p_type");
        sql.Should().Contain("\"flow_name\": \"workflow-manual\"");
        sql.Should().Contain("workflow.activate_flow('workflow-manual', 1)");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.signal_process(uuid, text, text, jsonb) TO course_publication");
    }

    [Fact]
    public void Idempotency_key_includes_manifest_scope_and_action_version()
    {
        string sql = ReadMigration("20260826180000_idempotency_scope_and_version.sql");
        sql.Should().Contain("ADD COLUMN IF NOT EXISTS action_version integer NOT NULL DEFAULT 1");
        sql.Should().Contain("PRIMARY KEY (principal, module, action, action_version, idempotency_key)");
        sql.Should().Contain("WHEN 'principal_action' THEN");
        sql.Should().Contain("WHEN 'consumer_action' THEN");
        sql.Should().Contain("WHEN 'global_action' THEN");
        sql.Should().Contain("AND k.action_version = v_row.version");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION api.invoke(");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO workflow_worker");
    }

    private static string ReadMigration(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "db", "migrations", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Migration {fileName} was not found.");
    }
}
