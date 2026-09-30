using FluentAssertions;
using ReliaPay.Contracts;

namespace ReliaPay.UnitTests.Data;

public sealed class JobLeaseUntilFencingTests
{
    private const string Migration = "20260930130000_job_lease_until_fencing.sql";

    [Fact]
    public void Finish_and_fail_job_require_non_expired_lease_until_before_mutation()
    {
        string sql = ReadMigration(Migration);

        sql.Should().Contain("CREATE OR REPLACE FUNCTION workflow.finish_job(");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION workflow.fail_job(");
        sql.Should().Contain("v_job.lease_until IS NULL");
        sql.Should().Contain("v_job.lease_until <= clock_timestamp()");
        sql.Should().Contain(ErrorCodes.WorkflowLeaseStale);

        AssertLeaseCheckBeforeMutation(sql, "CREATE OR REPLACE FUNCTION workflow.finish_job(");
        AssertLeaseCheckBeforeMutation(sql, "CREATE OR REPLACE FUNCTION workflow.fail_job(");

        sql.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO workflow_worker");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean) TO workflow_worker");
    }

    private static void AssertLeaseCheckBeforeMutation(string sql, string functionMarker)
    {
        int start = sql.IndexOf(functionMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        int leaseCheck = sql.IndexOf("v_job.lease_until <= clock_timestamp()", start, StringComparison.Ordinal);
        leaseCheck.Should().BeGreaterThan(start);
        int firstAttemptUpdate = sql.IndexOf("UPDATE workflow.attempts", leaseCheck, StringComparison.Ordinal);
        firstAttemptUpdate.Should().BeGreaterThan(leaseCheck);
        int firstJobUpdate = sql.IndexOf("UPDATE workflow.jobs", leaseCheck, StringComparison.Ordinal);
        firstJobUpdate.Should().BeGreaterThan(leaseCheck);
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
