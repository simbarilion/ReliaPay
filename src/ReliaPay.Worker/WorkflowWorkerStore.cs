using System.Text.Json;
using Npgsql;
using ReliaPay.Contracts;
using ReliaPay.Data;

namespace ReliaPay.Worker;

/// <summary>
/// Вызовы SECURITY DEFINER-функций роли <c>workflow_worker</c> без table DML.
/// </summary>
public sealed class WorkflowWorkerStore
{
    private readonly PostgresConnectionFactory _connections;

    /// <summary>
    /// Создаёт доступ к claim/finish/fail.
    /// </summary>
    /// <param name="connections">Фабрика роли worker.</param>
    public WorkflowWorkerStore(PostgresConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    /// <summary>
    /// Открывает соединение worker для invoke+finish на одной транзакции.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return OpenAsync(cancellationToken);
    }

    /// <summary>
    /// Автокоммитный захват одного READY/RETRY_WAIT/просроченного LEASED job.
    /// </summary>
    /// <param name="owner">Владелец lease.</param>
    /// <param name="leaseMs">Длительность lease.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<string> ClaimJobsAsync(string owner, int leaseMs, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"SELECT workflow.{WorkflowFunctions.ClaimJobs}(@owner, @lease)::text",
            connection);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("lease", leaseMs);
        return await ReadTextAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Успешное завершение job на уже открытой транзакции вместе с invoke.
    /// </summary>
    /// <param name="connection">Открытое соединение worker.</param>
    /// <param name="transaction">Транзакция invoke+finish.</param>
    /// <param name="job">Захваченный job.</param>
    /// <param name="outcome">Outcome target.</param>
    /// <param name="resultJson">JSON result для слияния в process data.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<string> FinishJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedJob job,
        string outcome,
        string resultJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        await using NpgsqlCommand command = new(
            $"SELECT workflow.{WorkflowFunctions.FinishJob}(@jobId, @owner, @leaseVersion, @outcome, @result::jsonb)::text",
            connection,
            transaction);
        command.Parameters.AddWithValue("jobId", job.JobId);
        command.Parameters.AddWithValue("owner", job.LeaseOwner);
        command.Parameters.AddWithValue("leaseVersion", job.LeaseVersion);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("result", resultJson);
        return await ReadTextAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Отдельный вызов fail после ROLLBACK invoke-транзакции.
    /// </summary>
    /// <param name="job">Захваченный job.</param>
    /// <param name="errorCode">Нормативный код ошибки.</param>
    /// <param name="retryable">Ставить ли RETRY_WAIT.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<string> FailJobAsync(
        ClaimedJob job,
        string errorCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"SELECT workflow.{WorkflowFunctions.FailJob}(@jobId, @owner, @leaseVersion, @errorCode, @retryable)::text",
            connection);
        command.Parameters.AddWithValue("jobId", job.JobId);
        command.Parameters.AddWithValue("owner", job.LeaseOwner);
        command.Parameters.AddWithValue("leaseVersion", job.LeaseVersion);
        command.Parameters.AddWithValue("errorCode", errorCode);
        command.Parameters.AddWithValue("retryable", retryable);
        return await ReadTextAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Код ошибки конверта либо null, если это не error.
    /// </summary>
    /// <param name="json">Текст jsonb-ответа.</param>
    public static string? ErrorCode(string json)
    {
        return EnvelopeCode(json);
    }

    /// <summary>
    /// Конверт finish/fail: чужой lease не меняет цикл worker.
    /// </summary>
    /// <param name="json">Текст jsonb-ответа.</param>
    public static bool IsLeaseStale(string json)
    {
        return EnvelopeCode(json) == ErrorCodes.WorkflowLeaseStale;
    }

    /// <summary>
    /// Конверт со <c>status=ok</c>.
    /// </summary>
    /// <param name="json">Текст jsonb-ответа.</param>
    public static bool IsOk(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("status", out JsonElement status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "ok", StringComparison.Ordinal);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _connections.OpenWorkerConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException exception) when (exception is not PostgresException)
        {
            throw new PostgresUnavailableException();
        }
    }

    private static async Task<string> ReadTextAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        try
        {
            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return scalar as string ?? Convert.ToString(scalar) ?? "{}";
        }
        catch (NpgsqlException exception) when (exception is not PostgresException)
        {
            throw new PostgresUnavailableException();
        }
    }

    private static string? EnvelopeCode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("status", out JsonElement status)
            || !string.Equals(status.GetString(), "error", StringComparison.Ordinal))
        {
            return null;
        }

        return root.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }
}
