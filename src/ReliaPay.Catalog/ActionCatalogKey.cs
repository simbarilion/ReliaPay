namespace ReliaPay.Catalog;

/// <summary>
/// Неизменяемый ключ опубликованной версии action в каталоге.
/// </summary>
/// <param name="Module">Сегмент модуля HTTP-маршрута, например <c>payment</c>.</param>
/// <param name="Action">Сегмент действия HTTP-маршрута, например <c>request</c>.</param>
/// <param name="Version">Целочисленная версия манифеста.</param>
public sealed record ActionCatalogKey(string Module, string Action, int Version)
{
    /// <summary>
    /// Канонический ключ вида <c>module.action</c> без версии.
    /// </summary>
    public string RouteKey => $"{Module}.{Action}";
}
