namespace ReliaPay.Cli;

/// <summary>
/// Читает файл карты/payload или stdin (<c>/dev/stdin</c>, <c>-</c>).
/// </summary>
internal static class CliInput
{
    /// <summary>
    /// Возвращает текст входного документа.
    /// </summary>
    public static async Task<string> ReadAsync(string path)
    {
        if (IsStdin(path))
        {
            return await Console.In.ReadToEndAsync().ConfigureAwait(false);
        }

        return await File.ReadAllTextAsync(path).ConfigureAwait(false);
    }

    /// <summary>
    /// Истина, если путь указывает на стандартный ввод контейнера.
    /// </summary>
    public static bool Exists(string path)
    {
        return IsStdin(path) || File.Exists(path);
    }

    /// <summary>
    /// Stdin-псевдопути автопроверки и POSIX.
    /// </summary>
    public static bool IsStdin(string path)
    {
        return string.Equals(path, "/dev/stdin", StringComparison.Ordinal)
            || string.Equals(path, "-", StringComparison.Ordinal);
    }
}
