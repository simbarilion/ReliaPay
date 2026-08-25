using ReliaPay.Contracts;

namespace ReliaPay.Cli;

/// <summary>
/// Разобранная команда Course CLI.
/// </summary>
public abstract record ParsedCliCommand;

/// <summary>Запуск без аргументов: apply встроенных миграций и idle.</summary>
public sealed record IdleCliCommand : ParsedCliCommand;

/// <summary>Команда <c>migration apply &lt;directory&gt;</c>.</summary>
/// <param name="Directory">Каталог SQL-файлов.</param>
public sealed record MigrationApplyCliCommand(string Directory) : ParsedCliCommand;

/// <summary>Команда <c>action validate &lt;manifest&gt;</c>.</summary>
/// <param name="ManifestPath">Путь к JSON-манифесту внутри контейнера.</param>
public sealed record ActionValidateCliCommand(string ManifestPath) : ParsedCliCommand;

/// <summary>Команда <c>action publish &lt;manifest&gt;</c>.</summary>
/// <param name="ManifestPath">Путь к JSON-манифесту внутри контейнера.</param>
public sealed record ActionPublishCliCommand(string ManifestPath) : ParsedCliCommand;

/// <summary>Команда <c>action list</c>.</summary>
public sealed record ActionListCliCommand : ParsedCliCommand;

/// <summary>Команда <c>action activate &lt;module.action&gt; --version &lt;version&gt;</c>.</summary>
/// <param name="Route">Ключ маршрута.</param>
/// <param name="Version">Версия манифеста.</param>
public sealed record ActionActivateCliCommand(string Route, int Version) : ParsedCliCommand;

/// <summary>Команда <c>action disable</c> с опциональным replacement.</summary>
/// <param name="Route">Ключ маршрута.</param>
/// <param name="Version">Выключаемая версия.</param>
/// <param name="ReplacementVersion">Новая default-версия, если нужна.</param>
public sealed record ActionDisableCliCommand(string Route, int Version, int? ReplacementVersion) : ParsedCliCommand;

/// <summary>Неверные аргументы или неизвестная команда.</summary>
/// <param name="Code">Код ошибки CLI.</param>
/// <param name="Message">Сообщение оператору.</param>
public sealed record InvalidCliCommand(string Code, string Message) : ParsedCliCommand;

/// <summary>
/// Разбирает argv контейнера cli в типизированную команду без обращения к PostgreSQL.
/// </summary>
public static class CliCommandParser
{
    /// <summary>
    /// Разбирает аргументы точки входа.
    /// </summary>
    /// <param name="args">Аргументы после entrypoint.</param>
    /// <returns>Команда или <see cref="InvalidCliCommand"/>.</returns>
    public static ParsedCliCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new IdleCliCommand();
        }

        if (EqualsOrd(args[0], "migration"))
        {
            if (args.Count == 3 && EqualsOrd(args[1], "apply") && !string.IsNullOrWhiteSpace(args[2]))
            {
                return new MigrationApplyCliCommand(args[2]);
            }

            return Invalid("migration apply <directory>");
        }

        if (!EqualsOrd(args[0], "action"))
        {
            return Invalid("unknown command");
        }

        if (args.Count < 2)
        {
            return Invalid("action validate|publish|list|activate|disable");
        }

        if (EqualsOrd(args[1], "validate"))
        {
            return args.Count == 3 && !string.IsNullOrWhiteSpace(args[2])
                ? new ActionValidateCliCommand(args[2])
                : Invalid("action validate <manifest>");
        }

        if (EqualsOrd(args[1], "publish"))
        {
            return args.Count == 3 && !string.IsNullOrWhiteSpace(args[2])
                ? new ActionPublishCliCommand(args[2])
                : Invalid("action publish <manifest>");
        }

        if (EqualsOrd(args[1], "list"))
        {
            return args.Count == 2
                ? new ActionListCliCommand()
                : Invalid("action list");
        }

        if (EqualsOrd(args[1], "activate"))
        {
            return ParseActivate(args);
        }

        if (EqualsOrd(args[1], "disable"))
        {
            return ParseDisable(args);
        }

        return Invalid("action validate|publish|list|activate|disable");
    }

    /// <summary>
    /// Разбирает <c>action activate</c> и флаг <c>--version</c>.
    /// </summary>
    private static ParsedCliCommand ParseActivate(IReadOnlyList<string> args)
    {
        if (!TryParseRouteAndFlags(args, skip: 2, allowReplacement: false, out string? route, out int? version, out _, out string? error))
        {
            return Invalid(error ?? "action activate <module.action> --version <version>");
        }

        if (route is null || version is null)
        {
            return Invalid("action activate <module.action> --version <version>");
        }

        return new ActionActivateCliCommand(route, version.Value);
    }

    /// <summary>
    /// Разбирает <c>action disable</c> и флаги version / replacement-version.
    /// </summary>
    private static ParsedCliCommand ParseDisable(IReadOnlyList<string> args)
    {
        if (!TryParseRouteAndFlags(args, skip: 2, allowReplacement: true, out string? route, out int? version, out int? replacement, out string? error))
        {
            return Invalid(error ?? "action disable <module.action> --version <version> [--replacement-version <version>]");
        }

        if (route is null || version is null)
        {
            return Invalid("action disable <module.action> --version <version> [--replacement-version <version>]");
        }

        return new ActionDisableCliCommand(route, version.Value, replacement);
    }

    /// <summary>
    /// Читает позиционный route и именованные флаги.
    /// </summary>
    private static bool TryParseRouteAndFlags(
        IReadOnlyList<string> args,
        int skip,
        bool allowReplacement,
        out string? route,
        out int? version,
        out int? replacement,
        out string? error)
    {
        route = null;
        version = null;
        replacement = null;
        error = null;
        for (int index = skip; index < args.Count; index++)
        {
            string token = args[index];
            if (TryReadFlag(token, "--version", args, ref index, out int? flagVersion, out error))
            {
                if (flagVersion is null)
                {
                    return false;
                }

                version = flagVersion;
                continue;
            }

            if (error is not null)
            {
                return false;
            }

            if (TryReadFlag(token, "--replacement-version", args, ref index, out int? flagReplacement, out error))
            {
                if (!allowReplacement)
                {
                    error = "unknown flag --replacement-version";
                    return false;
                }

                if (flagReplacement is null)
                {
                    return false;
                }

                replacement = flagReplacement;
                continue;
            }

            if (error is not null)
            {
                return false;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                error = "unknown flag " + token;
                return false;
            }

            if (route is not null)
            {
                error = "unexpected argument " + token;
                return false;
            }

            route = token;
        }

        return true;
    }

    /// <summary>
    /// Читает <c>--flag value</c> или <c>--flag=value</c> как целое ≥ 1.
    /// </summary>
    private static bool TryReadFlag(
        string token,
        string name,
        IReadOnlyList<string> args,
        ref int index,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;
        string prefix = name + "=";
        string raw;
        if (string.Equals(token, name, StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count)
            {
                error = name + " requires a value";
                return true;
            }

            index++;
            raw = args[index];
        }
        else if (token.StartsWith(prefix, StringComparison.Ordinal))
        {
            raw = token[prefix.Length..];
        }
        else
        {
            return false;
        }

        if (!int.TryParse(raw, out int parsed) || parsed < 1)
        {
            error = name + " must be an integer >= 1";
            return true;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Собирает ошибку разбора аргументов.
    /// </summary>
    private static InvalidCliCommand Invalid(string usage)
    {
        return new InvalidCliCommand(ErrorCodes.RequestInvalid, "invalid command arguments: " + usage);
    }

    /// <summary>
    /// Сравнение токена команды без учёта локали.
    /// </summary>
    private static bool EqualsOrd(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }
}
