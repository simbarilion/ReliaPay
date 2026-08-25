using System.Runtime.InteropServices;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;

namespace ReliaPay.Cli;

/// <summary>
/// Точка входа Course CLI. Без аргументов применяет встроенные миграции и остаётся в idle.
/// Явная команда <c>migration apply</c> выполняет тот же апплаер и завершается.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Разбирает аргументы контейнера и направляет их в команду или idle-цикл.
    /// </summary>
    /// <param name="args">Аргументы <c>docker compose run cli ...</c>.</param>
    /// <returns>Код процесса для Compose и автопроверки.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            string bundled = Environment.GetEnvironmentVariable("COURSE_MIGRATIONS_PATH") ?? "/migrations";
            int applyCode = await MigrationApplyCommand.ExecuteAsync(bundled, CancellationToken.None).ConfigureAwait(false);
            if (applyCode != 0)
            {
                return applyCode;
            }

            WriteReadyMarker();
            await WaitUntilShutdownAsync().ConfigureAwait(false);
            return 0;
        }

        if (args.Length == 3
            && string.Equals(args[0], "migration", StringComparison.Ordinal)
            && string.Equals(args[1], "apply", StringComparison.Ordinal))
        {
            int applyCode = await MigrationApplyCommand.ExecuteAsync(args[2], CancellationToken.None).ConfigureAwait(false);
            if (applyCode == 0)
            {
                WriteReadyMarker();
            }

            return applyCode;
        }

        await CliStdout.WriteAsync(
                CliErrorEnvelope.Create(
                    ErrorCodes.RequestInvalid,
                    "command is not implemented"))
            .ConfigureAwait(false);
        await Console.Error.WriteLineAsync("Supported command: migration apply <directory>.").ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// Сообщает Compose healthcheck, что checksum-миграции успешно применены.
    /// </summary>
    private static void WriteReadyMarker()
    {
        string path = Environment.GetEnvironmentVariable("COURSE_MIGRATION_READY_FILE")
            ?? (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? "/tmp/reliapay-migrated"
                : Path.Combine(Path.GetTempPath(), "reliapay-migrated"));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, "ok");
    }

    /// <summary>
    /// Держит процесс живым до SIGTERM/SIGINT, чтобы сервис cli в Compose оставался Up.
    /// </summary>
    /// <returns>Задача, завершающаяся при остановке контейнера.</returns>
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
