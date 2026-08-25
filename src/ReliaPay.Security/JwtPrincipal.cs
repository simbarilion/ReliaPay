namespace ReliaPay.Security;

/// <summary>
/// Результат явной проверки JWT: либо principal/consumer/scopes, либо отказ без 500.
/// </summary>
/// <param name="IsAuthenticated">Истина, если подпись, срок и JSON-типы claims приняты.</param>
/// <param name="Principal">Значение <c>sub</c>.</param>
/// <param name="Consumer">Значение <c>consumer</c>.</param>
/// <param name="Scopes">Разобранный claim <c>scope</c>.</param>
public sealed record JwtPrincipal(
    bool IsAuthenticated,
    string? Principal,
    string? Consumer,
    IReadOnlyList<string> Scopes)
{
    /// <summary>
    /// Собирает отказ без principal.
    /// </summary>
    public static JwtPrincipal Rejected { get; } = new(false, null, null, []);
}
