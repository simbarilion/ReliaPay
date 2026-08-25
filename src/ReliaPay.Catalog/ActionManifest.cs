using System.Text.Json;

namespace ReliaPay.Catalog;

/// <summary>
/// Опубликованный манифест action. Контрактные поля неизменяемы; <see cref="Enabled"/> и
/// <see cref="IsDefault"/> меняются только атомарными CLI-командами.
/// </summary>
/// <param name="Module">HTTP-сегмент модуля.</param>
/// <param name="Action">HTTP-сегмент действия.</param>
/// <param name="Version">Целочисленная версия манифеста.</param>
/// <param name="HttpMethod">На неделе 1 всегда POST.</param>
/// <param name="TargetSchema">Схема PostgreSQL с target-функцией.</param>
/// <param name="TargetFunction">Имя функции с сигнатурой (jsonb, jsonb) returns jsonb.</param>
/// <param name="RequestSchema">JSON Schema входного payload.</param>
/// <param name="ResponseSchema">JSON Schema поля result.</param>
/// <param name="Outcomes">Допустимые outcome в верхнем регистре.</param>
/// <param name="RequiredPolicy">Scopes, которые обязаны быть в доверенном context.</param>
/// <param name="IdempotencyMode"><c>required</c> или <c>none</c>.</param>
/// <param name="IdempotencyScope"><c>principal_action</c> или <c>none</c>.</param>
/// <param name="TimeoutMs">Лимит исполнения в миллисекундах.</param>
/// <param name="Enabled">Операционный флаг: версия включена для вызова.</param>
/// <param name="IsDefault">Операционный флаг: версия по умолчанию для маршрута.</param>
public sealed record ActionManifest(
    string Module,
    string Action,
    int Version,
    string HttpMethod,
    string TargetSchema,
    string TargetFunction,
    JsonElement RequestSchema,
    JsonElement ResponseSchema,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string> RequiredPolicy,
    string IdempotencyMode,
    string IdempotencyScope,
    int TimeoutMs,
    bool Enabled,
    bool IsDefault)
{
    /// <summary>
    /// Ключ маршрута <c>module.action</c> без версии.
    /// </summary>
    public string RouteKey => $"{Module}.{Action}";
}
