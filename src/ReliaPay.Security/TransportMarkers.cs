namespace ReliaPay.Security;

/// <summary>
/// Транспортные маркеры после generic HMAC. Secret и полная подпись сюда не попадают.
/// </summary>
/// <param name="SignatureVerified">Подпись проверена constant-time.</param>
/// <param name="SignatureVersion">Версия схемы подписи; для курса всегда 1.</param>
public sealed record TransportMarkers(bool SignatureVerified, int SignatureVersion);
