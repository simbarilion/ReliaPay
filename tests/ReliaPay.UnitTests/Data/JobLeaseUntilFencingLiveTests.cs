using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using ReliaPay.Contracts;

namespace ReliaPay.UnitTests.Data;

/// <summary>
/// Negative path: finish_job after lease_until expiry must fail with lease_stale
/// BEFORE any reclaim/claim. Polls the claimed leaseUntil instead of a blind sleep.
/// </summary>
[Collection("WorkflowPostgres")]
public sealed class JobLeaseUntilFencingLiveTests
{
    private readonly WorkflowPostgresFixture _fixture;

    public JobLeaseUntilFencingLiveTests(WorkflowPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [WorkflowDbFact]
    public async Task Finish_job_rejects_expired_lease_before_reclaim()
    {
        string owner = "worker-fence-" + Guid.NewGuid().ToString("N")[..8];
        await using NpgsqlConnection publication = await _fixture.Factory
            .OpenPublicationConnectionAsync(CancellationToken.None);
        using JsonDocument started = await QueryJsonAsync(
            publication,
            "SELECT workflow.start_process('workflow-smoke', @key, @data::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("key", "bk-fence-" + Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("data", """{"value":"fence"}""");
            });
        started.RootElement.GetProperty("status").GetString().Should().Be("ok");

        await using NpgsqlConnection worker = await _fixture.Factory
            .OpenWorkerConnectionAsync(CancellationToken.None);

        // Claim with 1 ms lease so expiry is observable without a long blind sleep.
        using JsonDocument claimed = await QueryJsonAsync(
            worker,
            "SELECT workflow.claim_jobs(@owner, 1)::text",
            command => command.Parameters.AddWithValue("owner", owner));
        claimed.RootElement.GetProperty("status").GetString().Should().Be("ok");
        claimed.RootElement.GetProperty("jobs").GetArrayLength().Should().BeGreaterThan(0);
        JsonElement job = claimed.RootElement.GetProperty("jobs")[0];
        Guid jobId = job.GetProperty("jobId").GetGuid();
        long leaseVersion = job.GetProperty("leaseVersion").GetInt64();
        DateTimeOffset leaseUntil = ParseLeaseUntil(job);

        await WaitUntilAsync(leaseUntil);

        using JsonDocument stale = await QueryJsonAsync(
            worker,
            "SELECT workflow.finish_job(@id, @owner, @ver, 'APPLIED', '{}'::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("id", jobId);
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("ver", leaseVersion);
            });
        stale.RootElement.GetProperty("status").GetString().Should().Be("error");
        stale.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.WorkflowLeaseStale);

        // Reclaim by a new owner, then finish once — proves the negative window was before reclaim.
        string owner2 = owner + "-reclaim";
        using JsonDocument reclaimed = await QueryJsonAsync(
            worker,
            "SELECT workflow.claim_jobs(@owner, 2000)::text",
            command => command.Parameters.AddWithValue("owner", owner2));
        reclaimed.RootElement.GetProperty("status").GetString().Should().Be("ok");
        reclaimed.RootElement.GetProperty("jobs").GetArrayLength().Should().BeGreaterThan(0);
        JsonElement job2 = reclaimed.RootElement.GetProperty("jobs")[0];
        Guid jobId2 = job2.GetProperty("jobId").GetGuid();
        long leaseVersion2 = job2.GetProperty("leaseVersion").GetInt64();
        jobId2.Should().Be(jobId);
        leaseVersion2.Should().BeGreaterThan(leaseVersion);

        using JsonDocument finished = await QueryJsonAsync(
            worker,
            "SELECT workflow.finish_job(@id, @owner, @ver, 'APPLIED', '{}'::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("id", jobId2);
                command.Parameters.AddWithValue("owner", owner2);
                command.Parameters.AddWithValue("ver", leaseVersion2);
            });
        finished.RootElement.GetProperty("status").GetString().Should().Be("ok");
    }

    private static DateTimeOffset ParseLeaseUntil(JsonElement job)
    {
        if (!job.TryGetProperty("leaseUntil", out JsonElement raw))
        {
            // Fallback: treat as already due so the first finish attempt exercises fencing.
            return DateTimeOffset.UtcNow.AddMilliseconds(-1);
        }

        string? text = raw.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return DateTimeOffset.UtcNow.AddMilliseconds(-1);
        }

        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static async Task WaitUntilAsync(DateTimeOffset leaseUntil)
    {
        for (int i = 0; i < 100; i++)
        {
            if (DateTimeOffset.UtcNow > leaseUntil)
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("lease_until did not expire within the poll budget.");
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
