namespace ReliaPay.Gateway.Configuration;

/// <summary>
/// Настройки связи шлюза с внутренним api. Шлюз не получает строки подключения PostgreSQL и JWT.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>
    /// Базовый URL внутреннего runtime внутри сети Compose, без завершающего слэша.
    /// </summary>
    public Uri ApiBaseUrl { get; init; } = new("http://api:8080");
}
