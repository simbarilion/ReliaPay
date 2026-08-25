using System.Text.Json;
using ReliaPay.Contracts.Envelopes;

namespace ReliaPay.Api.Http;

/// <summary>
/// Единые JSON-настройки HTTP-конвертов api: camelCase, без секретов в именах полей.
/// </summary>
public static class ApiJson
{
    /// <summary>
    /// Сериализация ответов runtime.
    /// </summary>
    public static JsonSerializerOptions ResponseOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Пишет ошибочный конверт с заданным статусом.
    /// </summary>
    /// <param name="statusCode">HTTP-статус матрицы.</param>
    /// <param name="envelope">Тело ошибки.</param>
    /// <returns>IResult с JSON.</returns>
    public static IResult Error(int statusCode, HttpErrorEnvelope envelope)
    {
        return Results.Json(envelope, ResponseOptions, statusCode: statusCode);
    }

    /// <summary>
    /// Пишет успешный конверт 200.
    /// </summary>
    /// <param name="envelope">Тело успеха.</param>
    /// <returns>IResult с JSON.</returns>
    public static IResult Ok(HttpSuccessEnvelope envelope)
    {
        return Results.Json(envelope, ResponseOptions);
    }
}
