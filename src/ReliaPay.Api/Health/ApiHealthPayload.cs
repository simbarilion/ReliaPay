namespace ReliaPay.Api.Health;

/// <summary>
/// Компактное тело health-ответа внутреннего runtime.
/// </summary>
/// <param name="Status">Краткий статус процесса: <c>live</c> или <c>ready</c>.</param>
public sealed record ApiHealthPayload(string Status);
