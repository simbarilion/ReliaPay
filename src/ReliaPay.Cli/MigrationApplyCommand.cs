using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Data;
using ReliaPay.Data.Migrations;

namespace ReliaPay.Cli;

/// <summary>
/// Команда <c>migration apply &lt;directory&gt;</c>: лексикографический прогон SQL с checksum в PostgreSQL.
/// </summary>
public static class MigrationApplyCommand
{
    /// <summary>
    /// Применяет каталог и печатает один JSON в stdout.
    /// </summary>
    /// <param name="directory">Путь внутри контейнера CLI.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ноль при успехе, ненулевой код при ошибке.</returns>
    public static async Task<int> ExecuteAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            PostgresEndpoints endpoints = PostgresEndpoints.FromEnvironment();
            PostgresConnectionFactory factory = new(endpoints);
            MigrationApplier applier = new(new PostgresMigrationJournal(factory));
            MigrationApplyResult result = await applier.ApplyAsync(directory, cancellationToken).ConfigureAwait(false);
            CliSuccessEnvelope envelope = new(
                "ok",
                new CliSuccessResult("migration", "applied", result.DirectoryPath, Version: result.AppliedCount),
                new CliMeta(CourseContracts.Version));
            await CliStdout.WriteAsync(envelope).ConfigureAwait(false);
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return await FailAsync(ErrorCodes.MigrationInvalid, "migration directory does not exist").ConfigureAwait(false);
        }
        catch (MigrationConflictException)
        {
            return await FailAsync(
                    ErrorCodes.MigrationConflict,
                    "applied migration file is immutable")
                .ConfigureAwait(false);
        }
        catch (PostgresUnavailableException)
        {
            await Console.Error.WriteLineAsync("PostgreSQL is unavailable.").ConfigureAwait(false);
            return await FailAsync(ErrorCodes.DependencyUnavailable, "PostgreSQL is unavailable").ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Environment variable", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("CLI database configuration is incomplete.").ConfigureAwait(false);
            return await FailAsync(ErrorCodes.MigrationInvalid, "CLI database configuration is incomplete").ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await Console.Error.WriteLineAsync("migration failed").ConfigureAwait(false);
            return await FailAsync(ErrorCodes.InternalError, "migration failed").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Пишет ошибочный конверт и возвращает код 1.
    /// </summary>
    /// <param name="code">Стабильный код ошибки.</param>
    /// <param name="message">Сообщение без SQL и connection string.</param>
    /// <returns>Ненулевой код выхода.</returns>
    private static async Task<int> FailAsync(string code, string message)
    {
        await CliStdout.WriteAsync(CliErrorEnvelope.Create(code, message)).ConfigureAwait(false);
        return 1;
    }
}
