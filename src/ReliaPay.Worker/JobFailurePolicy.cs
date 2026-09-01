using ReliaPay.Contracts;

namespace ReliaPay.Worker;

/// <summary>
/// Признак <c>retryable</c> для <c>workflow.fail_job</c>: повторяются timeout, недоступность и internal.
/// </summary>
public static class JobFailurePolicy
{
    /// <summary>
    /// Возвращает, нужно ли ставить job в RETRY_WAIT.
    /// </summary>
    /// <param name="code">Нормативный код ошибки.</param>
    /// <returns>Истина для временных сбоев.</returns>
    public static bool IsRetryable(string code)
    {
        return code is ErrorCodes.ActionTimeout
            or ErrorCodes.DependencyUnavailable
            or ErrorCodes.InternalError;
    }
}
