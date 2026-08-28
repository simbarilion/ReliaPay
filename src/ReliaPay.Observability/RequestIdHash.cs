using System.Security.Cryptography;
using System.Text;

namespace ReliaPay.Observability;

/// <summary>
/// Ограниченный отпечаток Idempotency-Key для журналов и трассировки. Сырой ключ в телескопию не попадает.
/// </summary>
public static class RequestIdHash
{
    /// <summary>
    /// Возвращает lowercase SHA-256 hex исходного ключа либо null, если ключа нет.
    /// </summary>
    /// <param name="rawIdempotencyKey">Заголовок Idempotency-Key.</param>
    /// <returns>64-символьный hex либо null.</returns>
    public static string? Sha256Hex(string? rawIdempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(rawIdempotencyKey))
        {
            return null;
        }

        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawIdempotencyKey.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
