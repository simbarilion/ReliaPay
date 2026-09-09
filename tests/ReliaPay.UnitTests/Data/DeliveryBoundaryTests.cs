using FluentAssertions;
using ReliaPay.Data;

namespace ReliaPay.UnitTests.Data;

public sealed class DeliveryBoundaryTests
{
    private const string Migration = "20260909010000_delivery_outbox_inbox.sql";

    [Fact]
    public void Python_execute_boundary_is_fixed_delivery_functions()
    {
        DeliveryFunctions.OutboxDispatcherExecute.Should().Equal(
            "claim_outbox",
            "succeed_outbox",
            "fail_outbox");
        DeliveryFunctions.InboxReconcilerExecute.Should().Equal("reconcile_inbox");
        DeliveryFunctions.OutboxDispatcherExecute.Should().NotContain(DeliveryFunctions.ReconcileInbox);
        DeliveryFunctions.InboxReconcilerExecute.Should().NotContain(DeliveryFunctions.ClaimOutbox);
        PostgresSchemas.Delivery.Should().Be("delivery");
    }

    [Fact]
    public void Delivery_migration_declares_tables_functions_views_and_least_privilege()
    {
        string sql = ReadMigration(Migration);

        sql.Should().Contain("CREATE TABLE delivery.external_requests");
        sql.Should().Contain("CREATE TABLE delivery.outbox");
        sql.Should().Contain("CREATE TABLE delivery.inbox");
        sql.Should().Contain("CREATE TABLE delivery.receipts");
        sql.Should().Contain("CREATE TABLE delivery.decisions");
        sql.Should().Contain("'PENDING'");
        sql.Should().Contain("'LEASED'");
        sql.Should().Contain("'RETRY_WAIT'");
        sql.Should().Contain("'DELIVERED'");
        sql.Should().Contain("'DEAD'");
        sql.Should().Contain("'CONFIRMED'");

        sql.Should().Contain("CREATE FUNCTION delivery.claim_outbox(p_owner text, p_limit integer)");
        sql.Should().Contain("outbox_id uuid");
        sql.Should().Contain("lease_version bigint");
        sql.Should().Contain("external_request_id text");
        sql.Should().Contain("correlation_id uuid");
        sql.Should().Contain("amount text");
        sql.Should().Contain("currency text");
        sql.Should().Contain("FOR UPDATE SKIP LOCKED");

        sql.Should().Contain("CREATE FUNCTION delivery.succeed_outbox(");
        sql.Should().Contain("p_provider_payment_id text");
        sql.Should().Contain("CREATE FUNCTION delivery.fail_outbox(");
        sql.Should().Contain("p_error_code text");
        sql.Should().Contain("CREATE FUNCTION delivery.reconcile_inbox(p_limit integer)");
        sql.Should().Contain("RETURNS integer");
        sql.Should().Contain("'CONFIRMED'");
        sql.Should().Contain("%.retryable");
        sql.Should().Contain("ARRAY[200, 400, 800]");

        sql.Should().Contain("CREATE VIEW autocheck.external_requests");
        sql.Should().Contain("CREATE VIEW autocheck.receipts");
        sql.Should().Contain("CREATE VIEW autocheck.outbox");
        sql.Should().Contain("CREATE VIEW autocheck.inbox");
        sql.Should().Contain("CREATE VIEW autocheck.decisions");

        sql.Should().Contain("GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO outbox_dispatcher");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION delivery.succeed_outbox(uuid, text, bigint, text) TO outbox_dispatcher");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION delivery.fail_outbox(uuid, text, bigint, text) TO outbox_dispatcher");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO inbox_reconciler");
        sql.Should().Contain("REVOKE ALL ON FUNCTION delivery.claim_outbox(text, integer) FROM PUBLIC");
        sql.Should().Contain("REVOKE ALL ON FUNCTION delivery.reconcile_inbox(integer) FROM PUBLIC");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO inbox_reconciler");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO outbox_dispatcher");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION api.invoke");
        sql.Should().NotContain("GRANT SELECT ON TABLE delivery.outbox TO outbox_dispatcher");
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
