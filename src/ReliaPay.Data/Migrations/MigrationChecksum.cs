using System.Security.Cryptography;

namespace ReliaPay.Data.Migrations;

/// <summary>
/// SHA-256 от содержимого файла миграции. Сравнение идёт по lowercase hex, как у payload_hash в autocheck.
/// </summary>
public static class MigrationChecksum
{
    /// <summary>
    /// Считает checksum сырых байт файла, без нормализации переводов строк.
    /// Файлы в репозитории хранятся с LF, чтобы Linux-контейнер и Git видели один и тот же хеш.
    /// </summary>
    /// <param name="content">Полное содержимое <c>.sql</c> файла.</param>
    /// <returns>64-символьный lowercase hex.</returns>
    public static string ComputeSha256Hex(ReadOnlySpan<byte> content)
    {
        byte[] hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
