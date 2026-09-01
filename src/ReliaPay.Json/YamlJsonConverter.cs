using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace ReliaPay.Json;

/// <summary>
/// Переводит YAML-карту в JSON, сохраняя числа и булевы значения, чтобы schema курса
/// видела integer, а не строки.
/// </summary>
public static class YamlJsonConverter
{
    /// <summary>
    /// Десериализует YAML и сериализует JSON-дерево.
    /// </summary>
    public static bool TryConvert(string yaml, out string json, out string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        json = string.Empty;
        errorMessage = null;
        try
        {
            object? parsed = new DeserializerBuilder()
                .WithAttemptingUnquotedStringTypeDeserialization()
                .Build()
                .Deserialize<object>(yaml);
            JsonNode? node = ToNode(parsed);
            if (node is null)
            {
                errorMessage = "YAML is empty";
                return false;
            }

            json = node.ToJsonString();
            return true;
        }
        catch (Exception)
        {
            errorMessage = "document is not valid YAML";
            return false;
        }
    }

    /// <summary>
    /// YamlDotNet отдаёт словари с ключами object; JSON Schema ждёт обычный JSON.
    /// </summary>
    private static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case IDictionary dictionary:
                JsonObject obj = [];
                foreach (DictionaryEntry entry in dictionary)
                {
                    obj[entry.Key?.ToString() ?? string.Empty] = ToNode(entry.Value);
                }

                return obj;
            case string text:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
                    && string.Equals(text, number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    return JsonValue.Create(number);
                }

                return text;
            case IList list:
                JsonArray array = [];
                foreach (object? item in list)
                {
                    array.Add(ToNode(item));
                }

                return array;
            case bool flag:
                return flag;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case float or double or decimal:
                return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
