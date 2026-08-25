namespace ReliaPay.Gateway.Health;

/// <summary>
/// Компактное тело health-ответа шлюза. Не подменяет контрактные action-конверты.
/// </summary>
/// <param name="Status">Краткий статус процесса или зависимости: <c>live</c>, <c>ready</c>, <c>unavailable</c>.</param>
public sealed record GatewayHealthPayload(string Status);
