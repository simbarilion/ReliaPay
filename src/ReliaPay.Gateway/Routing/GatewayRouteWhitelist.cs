using System.Text.RegularExpressions;

namespace ReliaPay.Gateway.Routing;

/// <summary>
/// Белый список внешних маршрутов шлюза. Клиент видит только actions, OpenAPI и health;
/// любые другие пути отсекаются до обращения к внутреннему api.
/// </summary>
public sealed class GatewayRouteWhitelist
{
    private static readonly Regex ActionRouteRegex = new(
        "^/api/[A-Za-z][A-Za-z0-9_-]*/[A-Za-z][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ActionOpenApiRegex = new(
        "^/openapi/actions/[A-Za-z][A-Za-z0-9_-]*/[A-Za-z][A-Za-z0-9_-]*/[0-9]+\\.json$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Проверяет, разрешён ли путь без query-строки политикой внешнего контура.
    /// </summary>
    /// <param name="path">Абсолютный путь HTTP-запроса, например <c>/api/payment/request</c>.</param>
    /// <returns>Истина, если маршрут входит в whitelist задания.</returns>
    public bool IsAllowed(string path)
    {
        string normalized = Normalize(path);
        if (normalized.Equals("/health/live", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("/health/ready", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("/openapi/default.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ActionRouteRegex.IsMatch(normalized) || ActionOpenApiRegex.IsMatch(normalized);
    }

    /// <summary>
    /// Отбрасывает query-строку и приводит пустой путь к корню.
    /// </summary>
    /// <param name="path">Исходный путь из HTTP-запроса.</param>
    /// <returns>Путь без query, начинающийся с <c>/</c>.</returns>
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        int queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        string withoutQuery = queryIndex >= 0 ? path[..queryIndex] : path;
        return withoutQuery.StartsWith('/') ? withoutQuery : "/" + withoutQuery;
    }
}
