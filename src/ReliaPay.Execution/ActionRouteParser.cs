using System.Text.RegularExpressions;

namespace ReliaPay.Execution;

/// <summary>
/// Разбор generic-маршрута <c>/api/{module}/{action}</c> и заголовка версии.
/// </summary>
public static class ActionRouteParser
{
    private static readonly Regex SegmentRegex = new(
        "^[A-Za-z][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Разбирает путь и необязательный <c>X-Action-Version</c>.
    /// </summary>
    /// <param name="path">Путь запроса без query.</param>
    /// <param name="versionHeader">Сырое значение заголовка версии.</param>
    /// <param name="route">Маршрут при успехе.</param>
    /// <returns>Истина, если путь и версия соответствуют контракту.</returns>
    public static bool TryParse(string path, string? versionHeader, out ActionRoute? route)
    {
        route = null;
        string normalized = path.Split('?', 2)[0];
        if (!normalized.StartsWith("/api/", StringComparison.Ordinal) || normalized.EndsWith('/'))
        {
            return false;
        }

        string rest = normalized["/api/".Length..];
        string[] parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !SegmentRegex.IsMatch(parts[0]) || !SegmentRegex.IsMatch(parts[1]))
        {
            return false;
        }

        int? version = null;
        if (!string.IsNullOrWhiteSpace(versionHeader))
        {
            if (!int.TryParse(versionHeader.Trim(), out int parsed) || parsed < 1)
            {
                return false;
            }

            version = parsed;
        }

        route = new ActionRoute(parts[0], parts[1], version);
        return true;
    }
}
