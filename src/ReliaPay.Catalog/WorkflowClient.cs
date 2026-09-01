using System.Text.Json;
using Npgsql;
using ReliaPay.Data;

namespace ReliaPay.Catalog;

/// <summary>
/// Вызовы SECURITY DEFINER-функций схемы <c>workflow</c> от роли publication.
/// </summary>
public sealed class WorkflowClient : IPublishedActionLookup
{
    private readonly PostgresConnectionFactory _connectionFactory;

    /// <summary>
    /// Создаёт клиент поверх publication-соединений.
    /// </summary>
    public WorkflowClient(PostgresConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Ищет опубликованную версию action для семантической проверки карты.
    /// </summary>
    public async Task<PublishedActionSnapshot?> FindActionAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenPublicationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                """
                SELECT module, action, version, enabled, outcomes::text, required_policy::text
                FROM api.actions
                WHERE module = @module AND action = @action AND version = @version
                """,
                connection);
            command.Parameters.AddWithValue("module", module);
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("version", version);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new PublishedActionSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                ReadStringArray(reader.GetString(4)),
                ReadStringArray(reader.GetString(5)));
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc cref="IPublishedActionLookup.FindAsync" />
    public Task<PublishedActionSnapshot?> FindAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        return FindActionAsync(module, action, version, cancellationToken);
    }

    /// <summary>Публикует карту.</summary>
    public Task<string> PublishAsync(string documentJson, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "SELECT workflow.publish_flow(@document::jsonb)::text",
            command => command.Parameters.AddWithValue("document", documentJson),
            cancellationToken);
    }

    /// <summary>Активирует версию карты.</summary>
    public Task<string> ActivateAsync(string flowName, int version, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "SELECT workflow.activate_flow(@name, @version)::text",
            command =>
            {
                command.Parameters.AddWithValue("name", flowName);
                command.Parameters.AddWithValue("version", version);
            },
            cancellationToken);
    }

    /// <summary>Список опубликованных версий.</summary>
    public Task<string> ListAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("SELECT workflow.list_flows()::text", _ => { }, cancellationToken);
    }

    /// <summary>Идемпотентный старт process.</summary>
    public Task<string> StartAsync(string flowName, string businessKey, string dataJson, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "SELECT workflow.start_process(@flow, @key, @data::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("flow", flowName);
                command.Parameters.AddWithValue("key", businessKey);
                command.Parameters.AddWithValue("data", dataJson);
            },
            cancellationToken);
    }

    /// <summary>Чтение process.</summary>
    public Task<string> GetAsync(Guid processId, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "SELECT workflow.get_process(@id)::text",
            command => command.Parameters.AddWithValue("id", processId),
            cancellationToken);
    }

    /// <summary>Идемпотентный сигнал.</summary>
    public Task<string> SignalAsync(
        Guid processId,
        string type,
        string messageId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "SELECT workflow.signal_process(@id, @type, @message, @payload::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("id", processId);
                command.Parameters.AddWithValue("type", type);
                command.Parameters.AddWithValue("message", messageId);
                command.Parameters.AddWithValue("payload", payloadJson);
            },
            cancellationToken);
    }

    /// <summary>Finish с fencing для тестового профиля.</summary>
    public Task<string> FinishJobAsync(
        Guid jobId,
        string owner,
        long leaseVersion,
        string outcome,
        string resultJson,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            "SELECT workflow.finish_job(@job, @owner, @lease, @outcome, @result::jsonb)::text",
            command =>
            {
                command.Parameters.AddWithValue("job", jobId);
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("lease", leaseVersion);
                command.Parameters.AddWithValue("outcome", outcome);
                command.Parameters.AddWithValue("result", resultJson);
            },
            cancellationToken);
    }

    private async Task<string> ExecuteAsync(
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenPublicationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(sql, connection);
            bind(command);
            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return scalar as string ?? Convert.ToString(scalar) ?? "{}";
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    private static IReadOnlyList<string> ReadStringArray(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        List<string> values = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    private static Exception Translate(NpgsqlException exception)
    {
        if (exception is PostgresException)
        {
            return new InvalidOperationException("workflow command failed");
        }

        return new PostgresUnavailableException();
    }
}
