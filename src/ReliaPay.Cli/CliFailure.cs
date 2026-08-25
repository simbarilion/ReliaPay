using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Data;

namespace ReliaPay.Cli;

/// <summary>
/// Общая запись ошибочного CLI-конверта и ненулевого кода выхода.
/// </summary>
internal static class CliFailure
{
    /// <summary>
    /// Пишет ошибку в stdout и возвращает 1.
    /// </summary>
    /// <param name="code">Стабильный код.</param>
    /// <param name="message">Сообщение без секретов.</param>
    /// <returns>Код выхода 1.</returns>
    public static async Task<int> WriteAsync(string code, string message)
    {
        await CliStdout.WriteAsync(CliErrorEnvelope.Create(code, message)).ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// Переводит типичные сбои PostgreSQL и конфигурации CLI в контрактные коды.
    /// </summary>
    /// <param name="exception">Исключение команды каталога.</param>
    /// <returns>Код выхода 1.</returns>
    public static async Task<int> FromExceptionAsync(Exception exception)
    {
        if (exception is PostgresUnavailableException)
        {
            await Console.Error.WriteLineAsync("PostgreSQL is unavailable.").ConfigureAwait(false);
            return await WriteAsync(ErrorCodes.DependencyUnavailable, "PostgreSQL is unavailable").ConfigureAwait(false);
        }

        if (exception is InvalidOperationException invalid
            && invalid.Message.Contains("Environment variable", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("CLI database configuration is incomplete.").ConfigureAwait(false);
            return await WriteAsync(ErrorCodes.RequestInvalid, "CLI database configuration is incomplete").ConfigureAwait(false);
        }

        await Console.Error.WriteLineAsync("catalog command failed").ConfigureAwait(false);
        return await WriteAsync(ErrorCodes.InternalError, "catalog command failed").ConfigureAwait(false);
    }
}
