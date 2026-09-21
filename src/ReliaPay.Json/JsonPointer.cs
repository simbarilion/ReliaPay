namespace ReliaPay.Json;

/// <summary>
/// RFC 6901 JSON Pointer: экранирование токенов и пересечение путей по decoded segments.
/// </summary>
public static class JsonPointer
{
    /// <summary>
    /// Экранирует один сегмент: <c>~</c> → <c>~0</c>, затем <c>/</c> → <c>~1</c>.
    /// </summary>
    public static string Escape(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Снимает экранирование сегмента: <c>~1</c> → <c>/</c>, затем <c>~0</c> → <c>~</c>.
    /// </summary>
    public static string Unescape(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
    }

    /// <summary>
    /// Имя поля без ведущего <c>/</c> становится pointer на корневое свойство.
    /// Строка, уже начинающаяся с <c>/</c>, считается готовым pointer.
    /// </summary>
    public static string FromPropertyName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.StartsWith('/'))
        {
            return name;
        }

        return "/" + Escape(name);
    }

    /// <summary>
    /// Пересечение назначений: одинаковый путь или предок/потомок по decoded segments.
    /// </summary>
    public static bool DestinationsOverlap(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!TryGetSegments(left, out string[] leftSegments)
            || !TryGetSegments(right, out string[] rightSegments))
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        return IsPrefix(leftSegments, rightSegments) || IsPrefix(rightSegments, leftSegments);
    }

    /// <summary>
    /// Разбирает pointer на decoded-сегменты. Пустой документный pointer не принимается.
    /// </summary>
    public static bool TryGetSegments(string pointer, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/')
        {
            return false;
        }

        segments = pointer.Split('/').Skip(1).Select(Unescape).ToArray();
        return true;
    }

    private static bool IsPrefix(string[] prefix, string[] other)
    {
        if (prefix.Length > other.Length)
        {
            return false;
        }

        for (int index = 0; index < prefix.Length; index++)
        {
            if (!string.Equals(prefix[index], other[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
