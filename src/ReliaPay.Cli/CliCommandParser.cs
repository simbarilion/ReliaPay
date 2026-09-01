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

/// <summary>Команда <c>flow validate &lt;map&gt;</c>.</summary>
/// <param name="MapPath">Путь к JSON/YAML карте или <c>/dev/stdin</c>.</param>
public sealed record FlowValidateCliCommand(string MapPath) : ParsedCliCommand;

/// <summary>Команда <c>flow publish &lt;map&gt;</c>.</summary>
/// <param name="MapPath">Путь к JSON/YAML карте или <c>/dev/stdin</c>.</param>
public sealed record FlowPublishCliCommand(string MapPath) : ParsedCliCommand;

/// <summary>Команда <c>flow list</c>.</summary>
public sealed record FlowListCliCommand : ParsedCliCommand;

/// <summary>Команда <c>flow activate &lt;flow_name&gt; --version &lt;version&gt;</c>.</summary>
/// <param name="FlowName">Имя карты.</param>
/// <param name="Version">Версия карты.</param>
public sealed record FlowActivateCliCommand(string FlowName, int Version) : ParsedCliCommand;

/// <summary>Команда <c>flow start</c>.</summary>
/// <param name="FlowName">Имя активной карты.</param>
/// <param name="BusinessKey">Идемпотентный ключ process.</param>
/// <param name="DataPath">JSON process data, stdin или пусто для <c>{}</c>.</param>
public sealed record FlowStartCliCommand(string FlowName, string BusinessKey, string DataPath) : ParsedCliCommand;

/// <summary>Команда <c>flow get &lt;processId&gt;</c>.</summary>
/// <param name="ProcessId">Идентификатор process.</param>
public sealed record FlowGetCliCommand(string ProcessId) : ParsedCliCommand;

/// <summary>Команда <c>flow signal</c>.</summary>
/// <param name="ProcessId">Идентификатор process.</param>
/// <param name="SignalType">Тип сигнала.</param>
/// <param name="MessageId">Глобально уникальный message id.</param>
/// <param name="PayloadPath">JSON тела сигнала или stdin.</param>
public sealed record FlowSignalCliCommand(
    string ProcessId,
    string SignalType,
    string MessageId,
    string PayloadPath) : ParsedCliCommand;

/// <summary>Команда <c>flow test-finish</c> в тестовом профиле.</summary>
/// <param name="JobId">Идентификатор job.</param>
/// <param name="Owner">Заявленный lease owner.</param>
/// <param name="LeaseVersion">Fencing token.</param>
/// <param name="Outcome">Outcome action.</param>
/// <param name="ResultPath">JSON result или stdin.</param>
public sealed record FlowTestFinishCliCommand(
    string JobId,
    string Owner,
    long LeaseVersion,
    string Outcome,
    string ResultPath) : ParsedCliCommand;

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
        return Parse(args, Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE"));
    }

    /// <summary>
    /// Разбирает аргументы с явно заданным тестовым профилем (для unit-тестов).
    /// </summary>
    public static ParsedCliCommand Parse(IReadOnlyList<string> args, string? courseTestProfile)
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

        if (EqualsOrd(args[0], "flow"))
        {
            return ParseFlow(args, string.Equals(courseTestProfile, "1", StringComparison.Ordinal));
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
    /// Разбирает семейство <c>flow ...</c>.
    /// </summary>
    private static ParsedCliCommand ParseFlow(IReadOnlyList<string> args, bool testProfile)
    {
        if (args.Count < 2)
        {
            return Invalid("flow validate|publish|list|activate|start|get|signal");
        }

        if (EqualsOrd(args[1], "validate"))
        {
            return args.Count == 3 && !string.IsNullOrWhiteSpace(args[2])
                ? new FlowValidateCliCommand(args[2])
                : Invalid("flow validate <map>");
        }

        if (EqualsOrd(args[1], "publish"))
        {
            return args.Count == 3 && !string.IsNullOrWhiteSpace(args[2])
                ? new FlowPublishCliCommand(args[2])
                : Invalid("flow publish <map>");
        }

        if (EqualsOrd(args[1], "list"))
        {
            return args.Count == 2
                ? new FlowListCliCommand()
                : Invalid("flow list");
        }

        if (EqualsOrd(args[1], "activate"))
        {
            return ParseFlowActivate(args);
        }

        if (EqualsOrd(args[1], "start"))
        {
            return ParseFlowStart(args);
        }

        if (EqualsOrd(args[1], "get"))
        {
            return args.Count == 3 && !string.IsNullOrWhiteSpace(args[2])
                ? new FlowGetCliCommand(args[2])
                : Invalid("flow get <processId>");
        }

        if (EqualsOrd(args[1], "signal"))
        {
            return ParseFlowSignal(args);
        }

        if (EqualsOrd(args[1], "test-finish"))
        {
            return testProfile
                ? ParseFlowTestFinish(args)
                : Invalid("unknown command");
        }

        return Invalid("flow validate|publish|list|activate|start|get|signal");
    }

    /// <summary>
    /// Разбирает <c>flow activate</c>.
    /// </summary>
    private static ParsedCliCommand ParseFlowActivate(IReadOnlyList<string> args)
    {
        if (!TryParseNamed(args, skip: 2, out string? name, out IReadOnlyDictionary<string, string> flags, out string? error))
        {
            return Invalid(error ?? "flow activate <flow_name> --version <version>");
        }

        if (name is null || !TryParsePositiveInt(flags, "--version", out int version))
        {
            return Invalid("flow activate <flow_name> --version <version>");
        }

        return new FlowActivateCliCommand(name, version);
    }

    /// <summary>
    /// Разбирает <c>flow start</c>.
    /// </summary>
    private static ParsedCliCommand ParseFlowStart(IReadOnlyList<string> args)
    {
        if (!TryParseNamed(args, skip: 2, out string? name, out IReadOnlyDictionary<string, string> flags, out string? error))
        {
            return Invalid(error ?? "flow start <flow_name> --business-key <key> [--data <file>]");
        }

        if (name is null
            || !flags.TryGetValue("--business-key", out string? key)
            || string.IsNullOrWhiteSpace(key))
        {
            return Invalid("flow start <flow_name> --business-key <key> [--data <file>]");
        }

        flags.TryGetValue("--data", out string? data);
        return new FlowStartCliCommand(name, key, data ?? string.Empty);
    }

    /// <summary>
    /// Разбирает <c>flow signal</c>.
    /// </summary>
    private static ParsedCliCommand ParseFlowSignal(IReadOnlyList<string> args)
    {
        if (!TryParseNamed(args, skip: 2, out string? processId, out IReadOnlyDictionary<string, string> flags, out string? error))
        {
            return Invalid(error ?? "flow signal <processId> --type <type> --message-id <id> --payload <file>");
        }

        if (processId is null
            || !flags.TryGetValue("--type", out string? type)
            || string.IsNullOrWhiteSpace(type)
            || !flags.TryGetValue("--message-id", out string? messageId)
            || string.IsNullOrWhiteSpace(messageId)
            || !flags.TryGetValue("--payload", out string? payload)
            || string.IsNullOrWhiteSpace(payload))
        {
            return Invalid("flow signal <processId> --type <type> --message-id <id> --payload <file>");
        }

        return new FlowSignalCliCommand(processId, type, messageId, payload);
    }

    /// <summary>
    /// Разбирает <c>flow test-finish</c>.
    /// </summary>
    private static ParsedCliCommand ParseFlowTestFinish(IReadOnlyList<string> args)
    {
        if (!TryParseNamed(args, skip: 2, out string? jobId, out IReadOnlyDictionary<string, string> flags, out string? error))
        {
            return Invalid(error ?? "flow test-finish <jobId> --owner <owner> --lease-version <n> --outcome <outcome> --result <file>");
        }

        if (jobId is null
            || !flags.TryGetValue("--owner", out string? owner)
            || string.IsNullOrWhiteSpace(owner)
            || !TryParseNonNegativeLong(flags, "--lease-version", out long leaseVersion)
            || !flags.TryGetValue("--outcome", out string? outcome)
            || string.IsNullOrWhiteSpace(outcome)
            || !flags.TryGetValue("--result", out string? result)
            || string.IsNullOrWhiteSpace(result))
        {
            return Invalid("flow test-finish <jobId> --owner <owner> --lease-version <n> --outcome <outcome> --result <file>");
        }

        return new FlowTestFinishCliCommand(jobId, owner, leaseVersion, outcome, result);
    }

    /// <summary>
    /// Читает один позиционный аргумент и именованные строковые флаги.
    /// </summary>
    private static bool TryParseNamed(
        IReadOnlyList<string> args,
        int skip,
        out string? positional,
        out IReadOnlyDictionary<string, string> flags,
        out string? error)
    {
        positional = null;
        Dictionary<string, string> collected = new(StringComparer.Ordinal);
        flags = collected;
        error = null;
        for (int index = skip; index < args.Count; index++)
        {
            string token = args[index];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                string name;
                string raw;
                int equals = token.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    name = token[..equals];
                    raw = token[(equals + 1)..];
                }
                else
                {
                    name = token;
                    if (index + 1 >= args.Count)
                    {
                        error = name + " requires a value";
                        return false;
                    }

                    index++;
                    raw = args[index];
                }

                if (string.IsNullOrWhiteSpace(raw) || collected.ContainsKey(name))
                {
                    error = "invalid flag " + name;
                    return false;
                }

                collected[name] = raw;
                continue;
            }

            if (positional is not null)
            {
                error = "unexpected argument " + token;
                return false;
            }

            positional = token;
        }

        return true;
    }

    /// <summary>
    /// Читает обязательный целый флаг ≥ 1.
    /// </summary>
    private static bool TryParsePositiveInt(IReadOnlyDictionary<string, string> flags, string name, out int value)
    {
        value = 0;
        return flags.TryGetValue(name, out string? raw)
            && int.TryParse(raw, out value)
            && value >= 1;
    }

    /// <summary>
    /// Читает неотрицательный bigint-флаг.
    /// </summary>
    private static bool TryParseNonNegativeLong(IReadOnlyDictionary<string, string> flags, string name, out long value)
    {
        value = 0;
        return flags.TryGetValue(name, out string? raw)
            && long.TryParse(raw, out value)
            && value >= 0;
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
