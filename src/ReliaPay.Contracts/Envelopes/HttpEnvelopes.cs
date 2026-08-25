using System.Text.Json;

namespace ReliaPay.Contracts.Envelopes;

/// <summary>
/// Метаданные HTTP-ответа, которые формирует runtime, а не клиент.
/// </summary>
/// <param name="CorrelationId">Серверный UUID корреляции запроса; обязателен после успешной аутентификации.</param>
/// <param name="ActionVersion">Фактически выбранная версия action либо null, если версия не резолвилась.</param>
public sealed record HttpResponseMeta(Guid? CorrelationId, int? ActionVersion);

/// <summary>
/// Успешный HTTP-конверт: HTTP 200 и <c>status=ok</c>.
/// </summary>
/// <param name="Status">Всегда <c>ok</c>.</param>
/// <param name="Outcome">Одно из значений <c>outcomes</c> манифеста.</param>
/// <param name="Result">Тело result, уже проверенное по response_schema.</param>
/// <param name="Meta">Серверные метаданные ответа.</param>
public sealed record HttpSuccessEnvelope(
    string Status,
    string Outcome,
    JsonElement Result,
    HttpResponseMeta Meta);

/// <summary>
/// Ошибочный HTTP-конверт единого вида для всех кодов матрицы.
/// </summary>
/// <param name="Status">Всегда <c>error</c>.</param>
/// <param name="Code">Нормативный код из <see cref="ErrorCodes"/>.</param>
/// <param name="Message">Человекочитаемое сообщение без SQL, stack trace и имён target.</param>
/// <param name="Retryable">Признак, можно ли безопасно повторить запрос.</param>
/// <param name="Details">Машиночитаемые детали; не содержит секретов и полного payload.</param>
/// <param name="Meta">Серверные метаданные; <c>correlationId</c> обязателен после аутентификации.</param>
public sealed record HttpErrorEnvelope(
    string Status,
    string Code,
    string Message,
    bool Retryable,
    IReadOnlyDictionary<string, string> Details,
    HttpResponseMeta Meta)
{
    /// <summary>
    /// Собирает конверт ошибки с пустым словарём деталей.
    /// </summary>
    /// <param name="code">Нормативный код ошибки.</param>
    /// <param name="message">Сообщение клиенту без внутренних утечек.</param>
    /// <param name="retryable">Можно ли повторить запрос.</param>
    /// <param name="correlationId">Серверный идентификатор корреляции либо null до аутентификации.</param>
    /// <param name="actionVersion">Выбранная версия action либо null.</param>
    /// <returns>Заполненный ошибочный конверт.</returns>
    public static HttpErrorEnvelope Create(
        string code,
        string message,
        bool retryable,
        Guid? correlationId,
        int? actionVersion)
    {
        return new HttpErrorEnvelope(
            Status: "error",
            Code: code,
            Message: message,
            Retryable: retryable,
            Details: new Dictionary<string, string>(),
            Meta: new HttpResponseMeta(correlationId, actionVersion));
    }
}
