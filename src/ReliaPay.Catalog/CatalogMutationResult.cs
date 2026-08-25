namespace ReliaPay.Catalog;

/// <summary>
/// Строка каталога для <c>action list</c>.
/// </summary>
/// <param name="Module">Сегмент модуля.</param>
/// <param name="Action">Сегмент действия.</param>
/// <param name="Version">Версия манифеста.</param>
/// <param name="Enabled">Версия включена для invoke.</param>
/// <param name="IsDefault">Версия по умолчанию для маршрута.</param>
public sealed record ActionListEntry(
    string Module,
    string Action,
    int Version,
    bool Enabled,
    bool IsDefault);

/// <summary>
/// Результат SECURITY DEFINER-команды каталога.
/// </summary>
/// <param name="IsSuccess">Истина, если PostgreSQL вернул <c>status=ok</c>.</param>
/// <param name="Code">Код ошибки каталога.</param>
/// <param name="Message">Сообщение без SQL.</param>
/// <param name="Resource">Тип ресурса при успехе.</param>
/// <param name="Operation">Имя операции при успехе.</param>
/// <param name="Key">Ключ <c>module.action</c>.</param>
/// <param name="Version">Версия манифеста.</param>
public sealed record CatalogMutationResult(
    bool IsSuccess,
    string? Code,
    string? Message,
    string Resource,
    string Operation,
    string Key,
    int? Version);
