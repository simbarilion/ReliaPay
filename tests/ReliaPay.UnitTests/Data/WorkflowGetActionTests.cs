using FluentAssertions;
using ReliaPay.Data;

namespace ReliaPay.UnitTests.Data;

public sealed class WorkflowGetActionTests
{
    [Fact]
    public void Migration_publishes_workflow_get_for_http_read()
    {
        string sql = ReadMigration("20260826120000_workflow_get_action.sql");
        sql.Should().Contain("CREATE FUNCTION app.workflow_get(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("\"module\": \"workflow\"");
        sql.Should().Contain("\"action\": \"get\"");
        sql.Should().Contain("\"required_policy\": [\"workflow:read\"]");
        sql.Should().Contain("api.activate_action('workflow.get', 1)");
        sql.Should().Contain("'process'");
        sql.Should().Contain("'steps'");
        sql.Should().Contain("'jobs'");
        sql.Should().Contain("'attempts'");
        sql.Should().Contain("REVOKE ALL ON FUNCTION app.workflow_get(jsonb, jsonb) FROM PUBLIC");
    }

    [Fact]
    public void Worker_connection_is_a_separate_role_from_runtime()
    {
        PostgresEndpoints endpoints = new(
            "postgres",
            5432,
            "course",
            "course_publication",
            "pub",
            "course_runtime",
            "run");
        endpoints.WorkerUser.Should().Be(PostgresRoles.Worker);
        endpoints.WorkerUser.Should().NotBe(endpoints.RuntimeUser);
        typeof(PostgresConnectionFactory).GetMethod(nameof(PostgresConnectionFactory.OpenWorkerConnectionAsync))
            .Should()
            .NotBeNull();
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
