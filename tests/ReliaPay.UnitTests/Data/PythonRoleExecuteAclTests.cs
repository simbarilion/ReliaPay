using FluentAssertions;

namespace ReliaPay.UnitTests.Data;

public sealed class PythonRoleExecuteAclTests
{
    private const string Migration = "20260909050000_python_role_execute_acl.sql";

    [Fact]
    public void Python_roles_lose_public_execute_on_week_one_trigger_functions()
    {
        string sql = ReadMigration(Migration);
        sql.Should().Contain("REVOKE EXECUTE ON FUNCTION app.operations_protect_identity() FROM PUBLIC");
        sql.Should().Contain("REVOKE EXECUTE ON FUNCTION app.operation_events_insert_only() FROM PUBLIC");
        sql.Should().Contain("REVOKE EXECUTE ON FUNCTION app.operations_protect_identity() FROM outbox_dispatcher");
        sql.Should().Contain("REVOKE EXECUTE ON FUNCTION app.operation_events_insert_only() FROM inbox_reconciler");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO outbox_dispatcher");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO inbox_reconciler");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION app.operations_protect_identity() TO outbox_dispatcher");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION api.invoke");
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
