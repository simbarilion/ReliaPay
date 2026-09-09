namespace ReliaPay.Security;

/// <summary>
/// Контекст, который C# собирает только после проверки JWT. Поля payload запроса
/// не имеют права подменить эти значения на границе <c>api.invoke</c>.
/// </summary>
/// <param name="Principal">Идентификатор субъекта из claim <c>sub</c>.</param>
/// <param name="Consumer">Канал-потребитель из claim <c>consumer</c>.</param>
/// <param name="Scopes">Набор полномочий из claim <c>scope</c>; источник — только проверенный JWT.</param>
/// <param name="CorrelationId">UUID следа запроса: mintит gateway, api только переиспользует.</param>
/// <param name="RequestId">Значение Idempotency-Key, если заголовок передан; иначе null.</param>
/// <param name="Deadline">Момент истечения, вычисленный из <c>timeout_ms</c> манифеста.</param>
/// <param name="ExecutionId">Идентификатор исполнения workflow-job; для HTTP равен null.</param>
/// <param name="ProcessId">Идентификатор process; для HTTP равен null.</param>
/// <param name="JobId">Идентификатор job; для HTTP равен null.</param>
/// <param name="AttemptId">Идентификатор попытки; для HTTP равен null.</param>
/// <param name="Transport">Маркеры HMAC; null, если заголовка подписи не было.</param>
public sealed record TrustedContext(
    string Principal,
    string Consumer,
    IReadOnlyList<string> Scopes,
    Guid CorrelationId,
    string? RequestId,
    DateTimeOffset Deadline,
    string? ExecutionId = null,
    string? ProcessId = null,
    string? JobId = null,
    string? AttemptId = null,
    TransportMarkers? Transport = null);
