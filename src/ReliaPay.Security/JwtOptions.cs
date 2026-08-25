namespace ReliaPay.Security;

/// <summary>
/// Параметры проверки JWT, которые api читает из окружения Compose.
/// </summary>
/// <param name="Issuer">Ожидаемый <c>iss</c>, для курса <c>moduledev-course</c>.</param>
/// <param name="Audience">Ожидаемый <c>aud</c>, для курса <c>moduledev-api</c>.</param>
/// <param name="SigningKey">Секрет HS256, не короче 32 байт. Не логировать.</param>
public sealed record JwtOptions(string Issuer, string Audience, string SigningKey)
{
    /// <summary>
    /// Читает JWT-параметры процесса api.
    /// </summary>
    /// <returns>Типизированные параметры подписи.</returns>
    /// <exception cref="InvalidOperationException">Переменная отсутствует или ключ короче 32 байт.</exception>
    public static JwtOptions FromEnvironment()
    {
        string issuer = ReadRequired("COURSE_JWT_ISSUER");
        string audience = ReadRequired("COURSE_JWT_AUDIENCE");
        string signingKey = ReadRequired("COURSE_JWT_SIGNING_KEY");
        if (System.Text.Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException("COURSE_JWT_SIGNING_KEY must be at least 32 bytes.");
        }

        return new JwtOptions(issuer, audience, signingKey);
    }

    /// <summary>
    /// Читает обязательную переменную без записи значения в логи.
    /// </summary>
    private static string ReadRequired(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable {name} is required.");
        }

        return value;
    }
}
