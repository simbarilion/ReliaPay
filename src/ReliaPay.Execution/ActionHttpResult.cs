using ReliaPay.Contracts.Envelopes;

namespace ReliaPay.Execution;

/// <summary>
/// Результат generic HTTP-исполнителя: либо успех 200, либо ошибка матрицы.
/// </summary>
public abstract record ActionHttpResult;

/// <summary>
/// Успешный вызов action: HTTP 200 и контрактный конверт.
/// </summary>
/// <param name="Envelope">Тело ответа.</param>
public sealed record ActionHttpSuccess(HttpSuccessEnvelope Envelope) : ActionHttpResult;

/// <summary>
/// Ошибка матрицы курса.
/// </summary>
/// <param name="StatusCode">HTTP-статус.</param>
/// <param name="Envelope">Единый error envelope.</param>
public sealed record ActionHttpFailure(int StatusCode, HttpErrorEnvelope Envelope) : ActionHttpResult;
