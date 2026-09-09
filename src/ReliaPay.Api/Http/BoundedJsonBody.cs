using ReliaPay.Contracts;

namespace ReliaPay.Api.Http;

/// <summary>
/// Читает тело action с явным верхним пределом, без полной буферизации сверх лимита.
/// HMAC считается над этими же байтами, поэтому текст не пересобирается до проверки подписи.
/// </summary>
internal static class BoundedJsonBody
{
    /// <summary>
    /// Читает исходные байты до <see cref="CourseHttpLimits.MaxRequestBodyBytes"/>. Null — лимит превышен.
    /// </summary>
    /// <param name="body">Входящий поток запроса.</param>
    /// <param name="contentLength">Заголовок Content-Length, если есть.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public static async Task<byte[]?> ReadBytesAsync(
        Stream body,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength > CourseHttpLimits.MaxRequestBodyBytes)
        {
            return null;
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > CourseHttpLimits.MaxRequestBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
