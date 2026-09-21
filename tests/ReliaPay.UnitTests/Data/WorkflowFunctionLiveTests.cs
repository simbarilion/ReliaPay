using System.Text.Json;
using FluentAssertions;
using Npgsql;
using ReliaPay.Contracts;
using ReliaPay.Data;
using ReliaPay.Data.Migrations;

namespace ReliaPay.UnitTests.Data;

[CollectionDefinition("WorkflowPostgres")]
public sealed class WorkflowPostgresCollection : ICollectionFixture<WorkflowPostgresFixture>
{
}

public sealed class WorkflowPostgresFixture : IAsyncLifetime
{
    public PostgresConnectionFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!WorkflowDbFactAttribute.IsEnabled)
        {
            return;
        }

        Factory = new PostgresConnectionFactory(PostgresEndpoints.FromEnvironment());
        MigrationApplier applier = new(new PostgresMigrationJournal(Factory));
        await applier.ApplyAsync(FindRepoPath("db", "migrations"), CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    internal static string FindRepoPath(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Repository path {string.Join('/', parts)} was not found.");
    }
}

public sealed class WorkflowDbFactAttribute : FactAttribute
{
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COURSE_DB_HOST"));

    public WorkflowDbFactAttribute()
    {
        if (!IsEnabled)
        {
            Skip = "Set COURSE_DB_HOST to run live workflow function tests against PostgreSQL.";
        }
    }
}

[Collection("WorkflowPostgres")]
public sealed class WorkflowFunctionLiveTests
{
    private readonly WorkflowPostgresFixture _fixture;

    public WorkflowFunctionLiveTests(WorkflowPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [WorkflowDbFact]
    public async Task Worker_role_executes_claim_and_finish_without_table_dml()
    {
        await using NpgsqlConnection worker = await _fixture.Factory
            .OpenWorkerConnectionAsync(CancellationToken.None);

        (await ScalarBoolAsync(
                worker,
                "SELECT has_table_privilege(current_user, 'workflow.jobs', 'SELECT')"))
            .Should()
            .BeFalse();
        (await ScalarBoolAsync(
                worker,
                "SELECT has_table_privilege(current_user, 'workflow.jobs', 'UPDATE')"))
            .Should()
            .BeFalse();
        (await ScalarBoolAsync(
                worker,
                "SELECT has_table_privilege(current_user, 'workflow.jobs', 'INSERT')"))
            .Should()
            .BeFalse();
        (await ScalarBoolAsync(
                worker,
                "SELECT has_function_privilege(current_user, 'workflow.claim_jobs(text, integer)', 'EXECUTE')"))
            .Should()
            .BeTrue();
        (await ScalarBoolAsync(
                worker,
                "SELECT has_function_privilege(current_user, 'workflow.finish_job(uuid, text, bigint, text, jsonb)', 'EXECUTE')"))
            .Should()
            .BeTrue();
        (await ScalarBoolAsync(
                worker,
                "SELECT has_function_privilege(current_user, 'workflow.start_process(text, text, jsonb)', 'EXECUTE')"))
            .Should()
            .BeFalse();
    }

    [WorkflowDbFact]
    public async Task Finish_job_rejects_foreign_owner_while_function_name_stays_the_same()
    {
        string owner = "worker-live-" + Guid.NewGuid().ToString("N")[..8];
        await using NpgsqlConnection publication = await _fixture.Factory
            .OpenPublicationConnectionAsync(CancellationToken.None);
        using JsonDocument started = await QueryJsonAsync(
            publication,
            "SELECT workflow.start_process('workflow-smoke', @key, @data::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("key", "bk-" + Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("data", """{"value":"live"}""");
            });
        started.RootElement.GetProperty("status").GetString().Should().Be("ok");

        await using NpgsqlConnection worker = await _fixture.Factory
            .OpenWorkerConnectionAsync(CancellationToken.None);
        using JsonDocument claimed = await QueryJsonAsync(
            worker,
            "SELECT workflow.claim_jobs(@owner, 2000)::text",
            command => command.Parameters.AddWithValue("owner", owner));
        claimed.RootElement.GetProperty("status").GetString().Should().Be("ok");
        claimed.RootElement.GetProperty("jobs").GetArrayLength().Should().BeGreaterThan(0);
        JsonElement job = claimed.RootElement.GetProperty("jobs")[0];
        Guid jobId = job.GetProperty("jobId").GetGuid();
        long leaseVersion = job.GetProperty("leaseVersion").GetInt64();

        using JsonDocument stale = await QueryJsonAsync(
            worker,
            "SELECT workflow.finish_job(@id, @owner, @ver, 'APPLIED', '{}'::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("id", jobId);
                command.Parameters.AddWithValue("owner", "intruder-" + owner);
                command.Parameters.AddWithValue("ver", leaseVersion);
            });
        stale.RootElement.GetProperty("status").GetString().Should().Be("error");
        stale.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.WorkflowLeaseStale);

        using JsonDocument finished = await QueryJsonAsync(
            worker,
            "SELECT workflow.finish_job(@id, @owner, @ver, 'APPLIED', '{}'::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("id", jobId);
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("ver", leaseVersion);
            });
        finished.RootElement.GetProperty("status").GetString().Should().Be("ok");
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return value is true;
    }

    private static async Task<JsonDocument> QueryJsonAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand> bind)
    {
        await using NpgsqlCommand command = new(sql, connection);
        bind(command);
        object? value = await command.ExecuteScalarAsync();
        string json = value as string
            ?? value?.ToString()
            ?? throw new InvalidOperationException("function returned no JSON.");
        return JsonDocument.Parse(json);
    }
}
