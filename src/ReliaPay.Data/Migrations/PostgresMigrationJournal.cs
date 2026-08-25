using Npgsql;
using ReliaPay.Observability;

namespace ReliaPay.Data.Migrations;

/// <summary>
/// Журнал миграций в <c>api.schema_migrations</c>. Пишет роль publication, DDL выполняет через SET ROLE owner.
/// </summary>
public sealed class PostgresMigrationJournal : IMigrationJournal
{
    private readonly PostgresConnectionFactory _connectionFactory;

    /// <summary>
    /// Создаёт журнал поверх фабрики publication-соединений.
    /// </summary>
    /// <param name="connectionFactory">Фабрика CLI-роли.</param>
    public PostgresMigrationJournal(PostgresConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<string?> GetChecksumAsync(string fileName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenPublicationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                "SELECT checksum FROM api.schema_migrations WHERE filename = @filename",
                connection);
            command.Parameters.AddWithValue("filename", fileName);
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is string checksum ? checksum : null;
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    public async Task ApplyInTransactionAsync(MigrationScript script, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        using var activity = ReliaPayInstrumentation.ActivitySource.StartActivity("cli.migration.apply");
        activity?.SetTag("migration.file", script.FileName);
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenPublicationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (NpgsqlCommand setRole = new("SET LOCAL ROLE course_owner", connection, transaction))
            {
                await setRole.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (NpgsqlCommand sql = new(script.Sql, connection, transaction))
            {
                await sql.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (NpgsqlCommand insert = new(
                             """
                             INSERT INTO api.schema_migrations (filename, checksum)
                             VALUES (@filename, @checksum)
                             """,
                             connection,
                             transaction))
            {
                insert.Parameters.AddWithValue("filename", script.FileName);
                insert.Parameters.AddWithValue("checksum", script.Checksum);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    /// <summary>
    /// Отделяет «база не отвечает» от ошибки SQL миграции, чтобы stdout не содержал текста сервера.
    /// </summary>
    /// <param name="exception">Исключение Npgsql.</param>
    /// <returns>Типизированная ошибка без SQL и connection string.</returns>
    private static Exception Translate(NpgsqlException exception)
    {
        if (exception is PostgresException)
        {
            return new InvalidOperationException("migration failed");
        }

        return new PostgresUnavailableException();
    }
}
