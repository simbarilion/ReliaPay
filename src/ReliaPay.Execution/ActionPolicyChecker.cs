using ReliaPay.Security;

namespace ReliaPay.Execution;

/// <summary>
/// Проверяет, что доверенный context содержит все scopes required_policy.
/// </summary>
public static class ActionPolicyChecker
{
    /// <summary>
    /// Возвращает истину, если каждый элемент policy есть в scopes JWT.
    /// </summary>
    /// <param name="requiredPolicy">Scopes манифеста.</param>
    /// <param name="principal">Проверенный JWT principal.</param>
    /// <returns>Истина при выполнении policy.</returns>
    public static bool IsSatisfied(IReadOnlyList<string> requiredPolicy, JwtPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(requiredPolicy);
        ArgumentNullException.ThrowIfNull(principal);
        if (!principal.IsAuthenticated)
        {
            return false;
        }

        foreach (string required in requiredPolicy)
        {
            if (!principal.Scopes.Contains(required, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
