using System.Diagnostics;

namespace ReliaPay.Observability;

/// <summary>
/// Единая точка регистрации источников трассировки ReliaPay.
/// Имя Activity совпадает во gateway, api и cli, чтобы коррелировать один клиентский запрос.
/// </summary>
public static class ReliaPayInstrumentation
{
    /// <summary>
    /// Имя <see cref="ActivitySource"/>, которое подписчики OpenTelemetry используют для подписки.
    /// </summary>
    public const string ActivitySourceName = "ReliaPay";

    /// <summary>
    /// Источник Activity для ручных spans вокруг invoke, CLI и проб готовности.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
}
