namespace ReliaPay.Observability;

/// <summary>
/// Скрывает секреты и граничные поля: JWT, cookie, ключи. Полный payload в журналы не кладётся.
/// </summary>
public static class LogRedaction
{
    /// <summary>
    /// Определяет, является ли HTTP-заголовок секретом и не должен попадать в лог и Activity.
    /// </summary>
    /// <param name="name">Имя заголовка.</param>
    /// <returns>Истина для Authorization, Cookie и заголовков секретов.</returns>
    public static bool IsSensitiveHeader(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Provider-Signature", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("signing", StringComparison.OrdinalIgnoreCase)
            || name.Contains("signature", StringComparison.OrdinalIgnoreCase)
            || name.Contains("jwt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("hmac", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Возвращает безопасное для журнала значение заголовка.
    /// </summary>
    /// <param name="name">Имя заголовка.</param>
    /// <param name="value">Исходное значение.</param>
    /// <returns>Маска <c>[redacted]</c> либо исходная строка.</returns>
    public static string RedactHeader(string name, string value)
    {
        return IsSensitiveHeader(name) ? "[redacted]" : value;
    }

    /// <summary>
    /// Определяет, нельзя ли оставлять тег Activity без маски.
    /// </summary>
    /// <param name="tagName">Имя тега.</param>
    /// <returns>Истина для authorization, cookie, payload и секретов.</returns>
    public static bool IsSensitiveTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        return tagName.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("payload", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("password", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("signing", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("signature", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("jwt", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("hmac", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("reason", StringComparison.OrdinalIgnoreCase)
            || tagName.Contains("receipt", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("message", StringComparison.OrdinalIgnoreCase)
            || tagName.EndsWith(".message", StringComparison.OrdinalIgnoreCase)
            || tagName.EndsWith(".reason", StringComparison.OrdinalIgnoreCase)
            || tagName.EndsWith(".body", StringComparison.OrdinalIgnoreCase);
    }
}
