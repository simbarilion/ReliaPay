namespace ReliaPay.Contracts;

/// <summary>
/// Нормативная матрица HTTP-статусов и признака retryable для кодов курса.
/// </summary>
public static class HttpErrorMatrix
{
    /// <summary>
    /// Возвращает HTTP-статус для кода ошибки. Неизвестные коды закрываются как 500.
    /// </summary>
    /// <param name="code">Нормативный код из <see cref="ErrorCodes"/>.</param>
    /// <returns>HTTP-статус ответа.</returns>
    public static int StatusCode(string code)
    {
        return code switch
        {
            ErrorCodes.AuthInvalid => 401,
            ErrorCodes.SignatureInvalid => 401,
            ErrorCodes.RequestInvalid => 400,
            ErrorCodes.IdempotencyRequired => 400,
            ErrorCodes.AccessDenied => 403,
            ErrorCodes.ReceiptSignatureRequired => 403,
            ErrorCodes.ActionNotFound => 404,
            ErrorCodes.OperationNotFound => 404,
            ErrorCodes.IdempotencyConflict => 409,
            ErrorCodes.PayloadInvalid => 422,
            ErrorCodes.ReceiptExternalRequestNotFound => 422,
            ErrorCodes.DependencyUnavailable => 503,
            ErrorCodes.ActionContractViolation => 500,
            ErrorCodes.ActionTimeout => 504,
            ErrorCodes.InternalError => 500,
            _ => 500,
        };
    }

    /// <summary>
    /// Можно ли безопасно повторить запрос при данном коде.
    /// </summary>
    /// <param name="code">Нормативный код ошибки.</param>
    /// <returns>Истина для временной недоступности и таймаута.</returns>
    public static bool Retryable(string code)
    {
        return code is ErrorCodes.DependencyUnavailable or ErrorCodes.ActionTimeout;
    }
}
