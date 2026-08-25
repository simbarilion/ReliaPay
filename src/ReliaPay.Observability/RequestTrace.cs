namespace ReliaPay.Observability;

/// <summary>
/// Серверный след одного HTTP-запроса: корреляция, маршрут и идентификатор идемпотентности.
/// </summary>
/// <param name="CorrelationId">UUID, который mintит gateway либо api, если заголовка нет.</param>
/// <param name="RequestId">Idempotency-Key, если передан.</param>
/// <param name="Route">Путь без query.</param>
/// <param name="Module">Сегмент модуля для action/OpenAPI либо null.</param>
/// <param name="Action">Сегмент действия либо null.</param>
public sealed record RequestTrace(
    Guid CorrelationId,
    string? RequestId,
    string Route,
    string? Module,
    string? Action);
