using System.Text.Json.Nodes;

namespace ReliaPay.Cli;

/// <summary>
/// Дополняет SQL-конверт CLI полями из таблицы задания, не удаляя уже существующие ключи.
/// </summary>
public static class WorkflowCliResult
{
    /// <summary>
    /// Копирует <c>version</c> в <c>flowVersion</c> и добавляет недостающие строки result.
    /// </summary>
    /// <param name="json">JSON ответа SECURITY DEFINER.</param>
    /// <param name="extra">Дополнительные поля result, только если ключа ещё нет.</param>
    /// <returns>Тот же конверт с аддитивными полями.</returns>
    public static string ApplyContractFields(string json, IReadOnlyDictionary<string, string>? extra = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonNode? parsed = JsonNode.Parse(json);
        if (parsed is not JsonObject root)
        {
            return json;
        }

        JsonObject target = root["result"] as JsonObject ?? root;
        if (target["version"] is not null && target["flowVersion"] is null)
        {
            target["flowVersion"] = target["version"]!.DeepClone();
        }

        if (extra is not null)
        {
            foreach ((string key, string value) in extra)
            {
                if (target[key] is null)
                {
                    target[key] = value;
                }
            }
        }

        return root.ToJsonString();
    }
}
