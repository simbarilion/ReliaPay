using FluentAssertions;

namespace ReliaPay.UnitTests.Data;

public sealed class TrainingCanaryTests
{
    [Fact]
    public void Migration_publishes_canary_and_both_smoke_map_versions()
    {
        string sql = ReadRepoFile("db", "migrations", "20260826130000_training_canary_and_smoke.sql");
        sql.Should().Contain("CREATE TABLE app.training_canary_effects");
        sql.Should().Contain("CREATE FUNCTION app.training_canary(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("\"module\": \"training\"");
        sql.Should().Contain("\"action\": \"canary\"");
        sql.Should().Contain("\"required_policy\": [\"workflow:execute\"]");
        sql.Should().Contain("\"outcomes\": [\"APPLIED\"]");
        sql.Should().Contain("api.activate_action('training.canary', 1)");
        sql.Should().Contain("workflow.publish_flow");
        sql.Should().Contain("\"flow_name\": \"workflow-smoke\"");
        sql.Should().Contain("\"version\": 1");
        sql.Should().Contain("\"version\": 2");
        sql.Should().Contain("workflow.activate_flow('workflow-smoke', 1)");
        sql.Should().Contain("REVOKE ALL ON FUNCTION app.training_canary(jsonb, jsonb) FROM PUBLIC");
    }

    [Fact]
    public void Bundled_manifest_and_maps_match_the_published_contract()
    {
        string manifest = ReadRepoFile("db", "manifests", "training.canary.v1.json");
        manifest.Should().Contain("\"module\": \"training\"");
        manifest.Should().Contain("\"action\": \"canary\"");
        manifest.Should().Contain("\"target_function\": \"training_canary\"");
        manifest.Should().Contain("\"workflow:execute\"");
        manifest.Should().Contain("\"APPLIED\"");

        string v1 = ReadRepoFile("db", "maps", "workflow-smoke.v1.json");
        v1.Should().Contain("\"flow_name\": \"workflow-smoke\"");
        v1.Should().Contain("\"version\": 1");
        v1.Should().Contain("\"action\": \"canary\"");
        v1.Should().Contain("\"outcome\": \"APPLIED\"");
        v1.Should().Contain("\"signal_type\": \"training.completed\"");

        string v2 = ReadRepoFile("db", "maps", "workflow-smoke.v2.json");
        v2.Should().Contain("\"flow_name\": \"workflow-smoke\"");
        v2.Should().Contain("\"version\": 2");
        v2.Should().Contain("\"timeout_ms\": 2500");
        v2.Should().Contain("\"action_version\": 1");

        string manual = ReadRepoFile("db", "maps", "workflow-manual.v1.json");
        manual.Should().Contain("\"flow_name\": \"workflow-manual\"");
        manual.Should().Contain("\"type\": \"manual\"");
        manual.Should().Contain("\"start_step\": \"review\"");
        manual.Should().Contain("\"allowed_outcomes\"");
    }

    [Fact]
    public void Canary_keeps_first_effect_on_execution_id_conflict()
    {
        string sql = ReadRepoFile("db", "migrations", "20260826170000_keep_first_pending_signal_manual.sql");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION app.training_canary(");
        sql.Should().Contain("ON CONFLICT (execution_id) DO NOTHING");
        sql.Should().NotContain("ON CONFLICT (execution_id) DO UPDATE");
        sql.Should().Contain("SELECT value INTO v_stored");
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
