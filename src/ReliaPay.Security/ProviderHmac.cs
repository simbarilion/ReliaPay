using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ReliaPay.Security;

/// <summary>
/// Результат generic-проверки <c>X-Provider-Signature</c> над исходными body bytes.
/// </summary>
public enum ProviderHmacResult
{
    /// <summary>Заголовка нет: остальные actions продолжают работу без transport markers.</summary>
    Absent,

    /// <summary>Формат <c>v1=&lt;lowercase hex&gt;</c> и HMAC-SHA256 совпали.</summary>
    Verified,

    /// <summary>Неверный формат, версия, ключ или digest. Target не вызывается.</summary>
    Invalid
}

/// <summary>
/// HMAC-SHA256 над точными HTTP body bytes. Ключ — raw UTF-8 секрет без trim/Base64/hex decode.
/// Сравнение digest — constant-time по decoded bytes.
/// </summary>
public static partial class ProviderHmac
{
    /// <summary>Контрактный заголовок подписи adapter → api.</summary>
    public const string HeaderName = "X-Provider-Signature";

    [GeneratedRegex("^v1=[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderFormat();

    /// <summary>
    /// Проверяет заголовок над <paramref name="body"/>. Не логирует ключ и подпись.
    /// </summary>
    /// <param name="key">UTF-8 bytes <c>PROVIDER_HMAC_SECRET</c>; пустой ключ не принимает подпись.</param>
    /// <param name="body">Исходные байты HTTP-тела.</param>
    /// <param name="header">Значение <see cref="HeaderName"/> либо null/пусто.</param>
    public static ProviderHmacResult Verify(ReadOnlySpan<byte> key, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return ProviderHmacResult.Absent;
        }

        if (key.IsEmpty || !HeaderFormat().IsMatch(header))
        {
            return ProviderHmacResult.Invalid;
        }

        byte[] expected = Convert.FromHexString(header.AsSpan("v1=".Length));
        byte[] actual = HMACSHA256.HashData(key, body);
        return CryptographicOperations.FixedTimeEquals(actual, expected)
            ? ProviderHmacResult.Verified
            : ProviderHmacResult.Invalid;
    }
}
