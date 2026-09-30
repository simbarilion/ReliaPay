using FluentAssertions;

namespace ReliaPay.UnitTests.Observability;

public sealed class DiagnosticsStalledTests
{
    private const string AgeMigration = "20260930140000_diagnostics_stalled_age.sql";
    private const string ObservabilityMigration = "20260928160000_observability.sql";

    [Fact]
    public void Stalled_age_migration_filters_by_dead_at_and_documented_interval()
    {
        string sql = ReadRepoFile("db", "migrations", AgeMigration);

        sql.Should().Contain("CREATE OR REPLACE FUNCTION app.diagnostics_stalled(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("app.diagnostics_stalled_after");
        sql.Should().Contain("interval '60 seconds'");
        sql.Should().Contain("o.dead_at IS NOT NULL");
        sql.Should().Contain("o.dead_at <= clock_timestamp() - v_threshold");
        sql.Should().Contain("o.state = 'DEAD'");
        sql.Should().Contain("p.state = 'WAITING_SIGNAL'");
        sql.Should().Contain("op.status = 'PROCESSING'");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION app.diagnostics_stalled(jsonb, jsonb) TO course_runtime");
        // Age stays in SQL — no C# age filter authority.
        sql.Should().NotContain("UPDATE delivery.outbox");
        sql.Should().NotContain("UPDATE app.operations");
    }

    [Fact]
    public void Observability_boot_still_publishes_diagnostics_stalled_action()
    {
        string sql = ReadRepoFile("db", "migrations", ObservabilityMigration);
        sql.Should().Contain("\"module\": \"diagnostics\"");
        sql.Should().Contain("\"action\": \"stalled\"");
        sql.Should().Contain("\"target_function\": \"diagnostics_stalled\"");
        sql.Should().Contain("diagnostics:read");
    }

    [Fact]
    public void Runbook_documents_stalled_threshold_from_dead_at()
    {
        string runbook = ReadRepoFile("docs", "runbook.md");
        runbook.Should().Contain("diagnostics.stalled");
        runbook.Should().Contain("dead_at");
        runbook.Should().Contain("60 секунд");
        runbook.Should().Contain("app.diagnostics_stalled_after");
        runbook.Should().Contain("interval '60 seconds'");
    }

    [Fact]
    public void Diagnostics_stalled_contract_files_remain_in_repo()
    {
        string root = RepoRoot();
        File.Exists(Path.Combine(root, "contracts", "course-1", "diagnostics-stalled.payload.schema.json"))
            .Should().BeTrue();
        File.Exists(Path.Combine(root, "contracts", "course-1", "diagnostics-stalled.result.schema.json"))
            .Should().BeTrue();
        string payload = File.ReadAllText(
            Path.Combine(root, "contracts", "course-1", "diagnostics-stalled.payload.schema.json"));
        payload.Should().Contain("\"type\": \"object\"");
        string result = File.ReadAllText(
            Path.Combine(root, "contracts", "course-1", "diagnostics-stalled.result.schema.json"));
        result.Should().Contain("items");
        result.Should().Contain("operationId");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReliaPay.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        string path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"File {string.Join('/', parts)} was not found.");
        }

        return File.ReadAllText(path);
    }
}
