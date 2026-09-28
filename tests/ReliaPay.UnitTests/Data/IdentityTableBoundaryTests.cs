using FluentAssertions;

namespace ReliaPay.UnitTests.Data;

/// <summary>
/// ??????? ???????????? Week 4: JobId / ExecutionId / AttemptId / LeaseVersion / ExternalRequestId / body.
/// </summary>
public sealed class IdentityTableBoundaryTests
{
    [Fact]
    public void Payment_prepare_binds_external_request_id_to_execution_id()
    {
        string sql = ReadRepoFile("db", "migrations", "20260909020000_payment_submit_and_maps.sql");
        sql.Should().Contain("v_external_id := NULLIF(p_context ->> 'executionId', '')");
        sql.Should().Contain("ON CONFLICT (external_request_id) DO NOTHING");
        sql.Should().Contain("INSERT INTO delivery.outbox");
        sql.Should().Contain("'externalRequestId', v_ext.external_request_id");
    }

    [Fact]
    public void Workflow_claim_grows_lease_version_and_mints_new_attempt_id()
    {
        string sql = ReadRepoFile("db", "migrations", "20260826150000_workflow_contract_fields.sql");
        sql.Should().Contain("workflow.claim_jobs");
        sql.Should().Contain("lease_version");
        sql.Should().Contain("attempt_id");
        sql.Should().Contain("FOR UPDATE SKIP LOCKED");
    }

    [Fact]
    public void Provider_body_helper_keeps_stable_operation_id_bytes()
    {
        string source = ReadRepoFile("python", "perimeter", "provider_body.py");
        source.Should().Contain("operationId");
        source.Should().Contain("separators");
        source.Should().Contain("amount");
        source.Should().Contain("currency");
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

        throw new InvalidOperationException($"File {string.Join('/', parts)} was not found.");
    }
}
