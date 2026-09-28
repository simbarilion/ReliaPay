using ReliaPay.Data;

namespace ReliaPay.Api.Metrics;

/// <summary>
/// Читает обязательные series наблюдаемости через SECURITY DEFINER функцию.
/// </summary>
internal sealed class CourseMetricsReader
{
    private readonly PostgresConnectionFactory _connections;

    public CourseMetricsReader(PostgresConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<byte[]> ReadOpenMetricsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenRuntimeConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM app.course_metrics()";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return OpenMetricsDocument.Empty();
        }

        double jobsReady = reader.GetDouble(0);
        double jobOldest = reader.GetDouble(1);
        double waiting = reader.GetDouble(2);
        double outboxPending = reader.GetDouble(3);
        double outboxOldest = reader.GetDouble(4);
        double failures = reader.GetDouble(5);

        return OpenMetricsDocument.Build(
        [
            ("workflow_jobs_ready", "gauge", "workflow_jobs_ready", jobsReady),
            ("workflow_job_oldest_age_seconds", "gauge", "workflow_job_oldest_age_seconds", jobOldest),
            ("workflow_processes_waiting", "gauge", "workflow_processes_waiting", waiting),
            ("outbox_pending", "gauge", "outbox_pending", outboxPending),
            ("outbox_oldest_age_seconds", "gauge", "outbox_oldest_age_seconds", outboxOldest),
            ("workflow_failures", "counter", "workflow_failures_total", failures),
        ]);
    }
}
