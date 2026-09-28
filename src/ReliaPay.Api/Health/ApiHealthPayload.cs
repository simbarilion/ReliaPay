using System.Text.Json.Serialization;

namespace ReliaPay.Api.Health;

/// <summary>
/// Минимальный JSON health-ответ внутреннего runtime.
/// </summary>
/// <param name="Status">Статус: <c>live</c>, <c>ready</c> или <c>not_ready</c>.</param>
/// <param name="Code">Код зависимости при <c>not_ready</c>; иначе null и не сериализуется.</param>
public sealed record ApiHealthPayload(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);
