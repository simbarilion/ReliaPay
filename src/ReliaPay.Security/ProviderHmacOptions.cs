using System.Text;

namespace ReliaPay.Security;

/// <summary>
/// Ключ HMAC receipt v1. Читается как есть, без trim и декодирования.
/// </summary>
/// <param name="Key">UTF-8 bytes секрета; пустой массив, если переменная не задана.</param>
public sealed record ProviderHmacOptions(byte[] Key)
{
    /// <summary>
    /// Читает <c>PROVIDER_HMAC_SECRET</c> без изменения пробелов. Отсутствие переменной
    /// оставляет ключ пустым: unsigned actions работают, signed — <c>signature.invalid</c>.
    /// </summary>
    public static ProviderHmacOptions FromEnvironment()
    {
        string? secret = Environment.GetEnvironmentVariable("PROVIDER_HMAC_SECRET");
        if (secret is null)
        {
            return new ProviderHmacOptions([]);
        }

        return new ProviderHmacOptions(Encoding.UTF8.GetBytes(secret));
    }
}
