namespace ReliaPay.Security;

/// <summary>
/// Контекст, который C# собирает только после проверки JWT. Поля payload запроса
/// не имеют права подменить эти значения на границе <c>api.invoke</c>.
/// </summary>
/// <param name="Principal">Идентификатор субъекта из claim <c>sub</c>.</param>
/// <param name="Consumer">Канал-потребитель из claim <c>consumer</c>.</param>
/// <param name="Scopes">Набор полномочий из claim <c>scope</c>; источник — только проверенный JWT.</param>
/// <param name="CorrelationId">UUID, сгенерированный runtime на каждый HTTP-запрос.</param>
/// <param name="RequestId">Значение Idempotency-Key, если заголовок передан; иначе null.</param>
/// <param name="Deadline">Момент истечения, вычисленный из <c>timeout_ms</c> манифеста.</param>
public sealed record TrustedContext(
    string Principal,
    string Consumer,
    IReadOnlyList<string> Scopes,
    Guid CorrelationId,
    string? RequestId,
    DateTimeOffset Deadline);
