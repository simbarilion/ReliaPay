using System.Text.Json;
using System.Text.Json.Nodes;
using ReliaPay.Contracts;

namespace ReliaPay.Execution;

/// <summary>
/// Собирает payload automatic-step по RFC 6901: <c>input_mapping</c> dest→source
/// из process data, затем накладывает <c>input_constants</c>.
/// </summary>
public static class WorkflowInputMapper
{
    /// <summary>
    /// Строит JSON-объект для <c>api.invoke</c>. Отсутствующий источник — <c>workflow.mapping_missing</c>.
    /// </summary>
    /// <param name="processData">Текущие process data.</param>
    /// <param name="mapping">Назначение JSON Pointer → источник JSON Pointer.</param>
    /// <param name="constants">Константы шага; ключ без <c>/</c> считается корневым полем.</param>
    /// <param name="payloadJson">Собранный объект либо пустая строка при ошибке.</param>
    /// <param name="errorCode">Нормативный код или null.</param>
    /// <returns>Истина, если payload собран.</returns>
    public static bool TryMap(
        JsonElement processData,
        IReadOnlyDictionary<string, string> mapping,
        JsonElement constants,
        out string payloadJson,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        JsonObject payload = new();
        foreach ((string destination, string source) in mapping)
        {
            if (!TryGetPointer(processData, source, out JsonElement found)
                || !TrySetPointer(payload, destination, CloneNode(found)))
            {
                payloadJson = string.Empty;
                errorCode = ErrorCodes.WorkflowMappingMissing;
                return false;
            }
        }

        if (constants.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in constants.EnumerateObject())
            {
                string destination = property.Name.StartsWith('/')
                    ? property.Name
                    : "/" + property.Name;
                if (!TrySetPointer(payload, destination, CloneNode(property.Value)))
                {
                    payloadJson = string.Empty;
                    errorCode = ErrorCodes.WorkflowMappingMissing;
                    return false;
                }
            }
        }

        payloadJson = payload.ToJsonString();
        errorCode = null;
        return true;
    }

    private static JsonNode? CloneNode(JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText());
    }

    private static bool TryGetPointer(JsonElement root, string pointer, out JsonElement value)
    {
        value = default;
        if (pointer is null)
        {
            return false;
        }

        if (pointer.Length == 0)
        {
            value = root;
            return true;
        }

        if (pointer[0] != '/')
        {
            return false;
        }

        JsonElement current = root;
        foreach (string token in pointer.Split('/').Skip(1))
        {
            string name = Unescape(token);
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(name, out current))
                {
                    return false;
                }

                continue;
            }

            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(
                    name,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int index)
                && index >= 0
                && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            return false;
        }

        value = current;
        return true;
    }

    private static bool TrySetPointer(JsonObject root, string pointer, JsonNode? value)
    {
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/')
        {
            return false;
        }

        string[] tokens = pointer.Split('/').Skip(1).Select(Unescape).ToArray();
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        JsonObject current = root;
        for (int index = 0; index < tokens.Length - 1; index++)
        {
            string token = tokens[index];
            if (current[token] is JsonObject nested)
            {
                current = nested;
                continue;
            }

            if (current[token] is not null)
            {
                return false;
            }

            JsonObject created = new();
            current[token] = created;
            current = created;
        }

        current[tokens[^1]] = value;
        return true;
    }

    private static string Unescape(string token)
    {
        return token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
}
