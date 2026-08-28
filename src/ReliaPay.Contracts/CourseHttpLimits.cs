namespace ReliaPay.Contracts;

/// <summary>
/// Согласованные HTTP-лимиты course-1: timeout манифеста, бюджет шлюза и размер JSON-тела.
/// </summary>
public static class CourseHttpLimits
{
    /// <summary>
    /// Верхняя граница <c>timeout_ms</c> манифеста, миллисекунды.
    /// </summary>
    public const int MaxTimeoutMs = 30_000;

    /// <summary>
    /// Timeout HTTP-клиента шлюза. Больше <see cref="MaxTimeoutMs"/>, чтобы api успел вернуть <c>action.timeout</c>.
    /// </summary>
    public const int GatewayHttpTimeoutSeconds = 35;

    /// <summary>
    /// Максимальный размер JSON-тела action, байты.
    /// </summary>
    public const int MaxRequestBodyBytes = 64 * 1024;
}
