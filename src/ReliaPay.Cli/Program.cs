using System.Runtime.InteropServices;
using ReliaPay.Contracts;

namespace ReliaPay.Cli;

/// <summary>
/// Точка входа Course CLI. Без аргументов применяет встроенные миграции и остаётся в idle.
/// С аргументами выполняет ровно одну контрактную команду и завершается.
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
        ParsedCliCommand command = CliCommandParser.Parse(args);
        return await DispatchAsync(command, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполняет разобранную команду.
    /// </summary>
    /// <param name="command">Результат разбора argv.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Код процесса.</returns>
    internal static async Task<int> DispatchAsync(ParsedCliCommand command, CancellationToken cancellationToken)
    {
        return command switch
        {
            IdleCliCommand => await RunIdleAsync(cancellationToken).ConfigureAwait(false),
            MigrationApplyCliCommand apply => await RunMigrationApplyAsync(apply.Directory, cancellationToken)
                .ConfigureAwait(false),
            ActionValidateCliCommand validate => await ActionValidateCommand.ExecuteAsync(validate.ManifestPath)
                .ConfigureAwait(false),
            ActionPublishCliCommand publish => await ActionPublishCommand
                .ExecuteAsync(publish.ManifestPath, cancellationToken)
                .ConfigureAwait(false),
            ActionListCliCommand => await ActionListCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            ActionActivateCliCommand activate => await ActionActivateCommand
                .ExecuteAsync(activate.Route, activate.Version, cancellationToken)
                .ConfigureAwait(false),
            ActionDisableCliCommand disable => await ActionDisableCommand
                .ExecuteAsync(disable.Route, disable.Version, disable.ReplacementVersion, cancellationToken)
                .ConfigureAwait(false),
            InvalidCliCommand invalid => await FailUsageAsync(invalid).ConfigureAwait(false),
            _ => await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "command is not implemented")
                .ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Применяет встроенные миграции и держит процесс живым для Compose.
    /// </summary>
    private static async Task<int> RunIdleAsync(CancellationToken cancellationToken)
    {
        string bundled = Environment.GetEnvironmentVariable("COURSE_MIGRATIONS_PATH") ?? "/migrations";
        int applyCode = await MigrationApplyCommand.ExecuteAsync(bundled, cancellationToken).ConfigureAwait(false);
        if (applyCode != 0)
        {
            return applyCode;
        }

        WriteReadyMarker();
        await WaitUntilShutdownAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Явный <c>migration apply</c>: тот же апплаер, процесс завершается.
    /// </summary>
    private static async Task<int> RunMigrationApplyAsync(string directory, CancellationToken cancellationToken)
    {
        int applyCode = await MigrationApplyCommand.ExecuteAsync(directory, cancellationToken).ConfigureAwait(false);
        if (applyCode == 0)
        {
            WriteReadyMarker();
        }

        return applyCode;
    }

    /// <summary>
    /// Пишет ошибочный конверт и подсказку по командам в stderr.
    /// </summary>
    private static async Task<int> FailUsageAsync(InvalidCliCommand invalid)
    {
        await Console.Error.WriteLineAsync(
                "Supported commands: migration apply <directory>; action validate|publish <manifest>; action list; action activate|disable <module.action> --version <version> [--replacement-version <version>].")
            .ConfigureAwait(false);
        return await CliFailure.WriteAsync(invalid.Code, invalid.Message).ConfigureAwait(false);
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
