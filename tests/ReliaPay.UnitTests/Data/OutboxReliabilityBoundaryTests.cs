using FluentAssertions;

namespace ReliaPay.UnitTests.Data;

public sealed class OutboxReliabilityBoundaryTests
{
    private const string Migration = "20260928120000_outbox_reliability.sql";

    [Fact]
    public void Reliability_migration_adds_dead_at_and_lease_fencing()
    {
        string sql = ReadMigration(Migration);

        sql.Should().Contain("ADD COLUMN IF NOT EXISTS dead_at timestamptz");
        sql.Should().Contain("delivery.lease_stale");
        sql.Should().Contain("lease_owner IS DISTINCT FROM btrim(p_owner)");
        sql.Should().Contain("lease_version IS DISTINCT FROM p_lease_version");
        sql.Should().Contain("o.state = 'LEASED'");
        sql.Should().Contain("o.lease_until < clock_timestamp()");
        sql.Should().Contain("FOR UPDATE SKIP LOCKED");
        sql.Should().Contain("app.outbox_max_attempts");
        sql.Should().Contain("app.outbox_jitter_max_ms");
        sql.Should().Contain("%.retryable");
        sql.Should().Contain("v_state := 'DEAD'");
        sql.Should().Contain("dead_at = COALESCE(dead_at, clock_timestamp())");
    }

    [Fact]
    public void Late_receipt_confirms_delivery_without_clearing_dead_at_or_rejecting_operation()
    {
        string sql = ReadMigration(Migration);

        sql.Should().Contain("Late receipt");
        sql.Should().Contain("SET state = 'CONFIRMED'");
        // CONFIRMED update must not wipe diagnostics (avoid NULLIF false-positive).
        sql.Should().NotContain("dead_at = NULL");
        sql.Should().NotContain("last_error_code = NULL;");
        sql.Should().NotContain("attempt_count = 0");
        // Outbox DEAD path must not mutate domain operation status.
        sql.Should().NotContain("UPDATE app.operations");
        sql.Should().NotContain("SET status = 'REJECTED'");
        sql.Should().NotContain("process_state = 'FAILED'");
        sql.Should().Contain("CREATE VIEW autocheck.outbox");
        sql.Should().Contain("dead_at");
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
