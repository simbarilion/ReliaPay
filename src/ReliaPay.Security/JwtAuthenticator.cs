using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ReliaPay.Security;

/// <summary>
/// Проверяет Bearer JWT по подписи и явно по JSON-типам claims. Неверный тип даёт отказ, не 500.
/// </summary>
public sealed class JwtAuthenticator
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly TokenValidationParameters _validation;

    /// <summary>
    /// Создаёт проверку HS256 с issuer/audience из конфигурации.
    /// </summary>
    /// <param name="options">Секрет и ожидаемые iss/aud.</param>
    public JwtAuthenticator(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] keyBytes = Encoding.UTF8.GetBytes(options.SigningKey);
        _validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };
        _handler.MapInboundClaims = false;
    }

    /// <summary>
    /// Разбирает заголовок Authorization и проверяет токен.
    /// </summary>
    /// <param name="authorizationHeader">Значение заголовка либо null.</param>
    /// <returns>Типизированный principal либо <see cref="JwtPrincipal.Rejected"/>.</returns>
    public async Task<JwtPrincipal> AuthenticateAsync(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return JwtPrincipal.Rejected;
        }

        string token = authorizationHeader["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return JwtPrincipal.Rejected;
        }

        TokenValidationResult validation;
        try
        {
            validation = await _handler.ValidateTokenAsync(token, _validation).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return JwtPrincipal.Rejected;
        }

        if (!validation.IsValid)
        {
            return JwtPrincipal.Rejected;
        }

        if (!TryReadPayload(token, out string? principal, out string? consumer, out IReadOnlyList<string>? scopes)
            || principal is null
            || consumer is null
            || scopes is null)
        {
            return JwtPrincipal.Rejected;
        }

        return new JwtPrincipal(true, principal, consumer, scopes);
    }

    /// <summary>
    /// Проверяет JSON-типы обязательных claims в payload, а не в слое Claim-строк.
    /// </summary>
    private static bool TryReadPayload(
        string token,
        out string? principal,
        out string? consumer,
        out IReadOnlyList<string>? scopes)
    {
        principal = null;
        consumer = null;
        scopes = null;
        JsonDocument document;
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            document = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
        }
        catch (Exception)
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (!TryReadStringClaim(root, "sub", out principal)
                || !TryReadStringClaim(root, "consumer", out consumer)
                || !TryReadScopes(root, out scopes)
                || !IsJsonNumber(root, "iat")
                || !IsJsonNumber(root, "exp"))
            {
                return false;
            }
        }

        return principal is not null && consumer is not null && scopes is not null;
    }

    /// <summary>
    /// Читает обязательный строковый claim.
    /// </summary>
    private static bool TryReadStringClaim(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Принимает <c>scope</c> как JSON-строку с пробелами или массив строк. Число и объект — отказ.
    /// </summary>
    private static bool TryReadScopes(JsonElement root, out IReadOnlyList<string>? scopes)
    {
        scopes = null;
        if (!root.TryGetProperty("scope", out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            string text = element.GetString() ?? string.Empty;
            scopes = string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            List<string> values = [];
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string? value = item.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                values.Add(value);
            }

            scopes = values;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Требует JSON-число для числовых зарегистрированных claims.
    /// </summary>
    private static bool IsJsonNumber(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number;
    }

    /// <summary>
    /// Декодирует base64url payload JWT.
    /// </summary>
    private static byte[] Base64UrlDecode(string payload)
    {
        string padded = payload.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }
}
