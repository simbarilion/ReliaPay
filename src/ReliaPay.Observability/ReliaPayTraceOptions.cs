namespace ReliaPay.Observability;

/// <summary>
/// Настройки HTTP-трассировки процесса: кто mintит correlationId.
/// </summary>
public sealed class ReliaPayTraceOptions
{
    /// <summary>
    /// Имя сервиса в OpenTelemetry Resource: <c>ReliaPay.Gateway</c> или <c>ReliaPay.Api</c>.
    /// </summary>
    public string ServiceName { get; init; } = "ReliaPay";

    /// <summary>
    /// Если истина, входящий <c>X-Correlation-Id</c> игнорируется и mintится новый UUID.
    /// </summary>
    public bool MintCorrelationId { get; init; }
}
