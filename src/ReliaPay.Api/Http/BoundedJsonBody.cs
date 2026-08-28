using ReliaPay.Contracts;

namespace ReliaPay.Api.Http;

/// <summary>
/// Читает JSON-тело action с явным верхним пределом, без полной буферизации сверх лимита.
/// </summary>
internal static class BoundedJsonBody
{
    /// <summary>
    /// Читает тело до <see cref="CourseHttpLimits.MaxRequestBodyBytes"/>. Null означает, что лимит превышен.
    /// </summary>
    /// <param name="body">Входящий поток запроса.</param>
    /// <param name="contentLength">Заголовок Content-Length, если есть.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Текст тела либо null при превышении лимита.</returns>
    public static async Task<string?> ReadAsync(
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

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
