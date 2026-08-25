using System.Runtime.InteropServices;
using System.Text.Json;
using ReliaPay.Contracts.Envelopes;

namespace ReliaPay.Cli;

/// <summary>
/// Точка входа Course CLI. Команды контракта подключаются отдельным этапом;
/// без аргументов процесс остаётся в idle, чтобы сервис Compose считался запущенным.
/// </summary>
public sealed class Program
{
    private static readonly JsonSerializerOptions StdoutJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Разбирает аргументы контейнера: пустой список — idle, иначе контрактная заглушка ошибки в stdout.
    /// </summary>
    /// <param name="args">Аргументы <c>docker compose run cli ...</c>.</param>
    /// <returns>Ноль для idle, ненулевой код при вызове ещё не реализованной команды.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            await WaitUntilShutdownAsync().ConfigureAwait(false);
            return 0;
        }

        CliErrorEnvelope envelope = CliErrorEnvelope.Create(
            "cli.not_implemented",
            "course CLI commands are not implemented in the solution skeleton");
        string json = JsonSerializer.Serialize(envelope, StdoutJson);
        await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        await Console.Error.WriteLineAsync("Pass migration/action commands after the CLI image is completed.").ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// Держит процесс живым до SIGTERM/SIGINT, чтобы <c>docker compose up</c> видел сервис cli запущенным.
    /// </summary>
    /// <returns>Задача, завершающаяся при сигнале остановки контейнера.</returns>
    private static async Task WaitUntilShutdownAsync()
    {
        using var shutdown = new CancellationTokenSource();
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => shutdown.Cancel());
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => shutdown.Cancel());
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await Console.Error.WriteLineAsync(
                "ReliaPay CLI is idle. Invoke commands with docker compose run --rm -T cli <command>.")
            .ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }
    }
}
