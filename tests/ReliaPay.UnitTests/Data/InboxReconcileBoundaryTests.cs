using FluentAssertions;

namespace ReliaPay.UnitTests.Data;

public sealed class InboxReconcileBoundaryTests
{
    private const string Migration = "20260909040000_inbox_reconcile_and_manual.sql";

    [Fact]
    public void Migration_applies_ready_inbox_and_leaves_early_received()
    {
        string sql = ReadRepoFile("db", "migrations", Migration);
        sql.Should().Contain("CREATE OR REPLACE FUNCTION delivery.reconcile_inbox(p_limit integer)");
        sql.Should().Contain("RETURNS integer");
        sql.Should().Contain("WAITING_SIGNAL");
        sql.Should().Contain("payment.receipt");
        sql.Should().Contain("'APPLIED'");
        sql.Should().Contain("'RECEIVED'");
        sql.Should().Contain("workflow._apply_pending_signal");
        sql.Should().Contain("INSERT INTO workflow.signals");
        sql.Should().Contain("delivery.inbox");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION workflow._apply_pending_signal(p_process_id uuid)");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION workflow._apply_pending_signal(uuid) TO inbox_reconciler");
        sql.Should().NotContain("GRANT SELECT ON TABLE delivery.inbox TO inbox_reconciler");
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
