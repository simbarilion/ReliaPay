using System.Text.Json;
using System.Text.Json.Nodes;
using ReliaPay.Catalog;
using ReliaPay.Contracts;

namespace ReliaPay.OpenApi;

/// <summary>
/// Собирает JSON OpenAPI 3 из опубликованных манифестов. Документы отдаются как JSON, не через Swagger UI.
/// </summary>
public sealed class OpenApiDocumentFactory
{
    /// <summary>
    /// Документ только включённых default-маршрутов.
    /// </summary>
    /// <param name="defaults">Манифесты с <c>enabled</c> и <c>is_default</c>.</param>
    /// <returns>OpenAPI-документ.</returns>
    public JsonObject CreateDefault(IReadOnlyList<ActionManifest> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        JsonObject paths = [];
        foreach (ActionManifest manifest in defaults)
        {
            paths[$"/api/{manifest.Module}/{manifest.Action}"] = CreatePathItem(manifest);
        }

        return CreateDocument("ReliaPay default actions", paths);
    }

    /// <summary>
    /// Документ одной точной версии action.
    /// </summary>
    /// <param name="manifest">Опубликованный манифест.</param>
    /// <returns>OpenAPI-документ.</returns>
    public JsonObject CreateAction(ActionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        JsonObject paths = new()
        {
            [$"/api/{manifest.Module}/{manifest.Action}"] = CreatePathItem(manifest),
        };
        return CreateDocument($"ReliaPay {manifest.RouteKey} v{manifest.Version}", paths);
    }

    private static JsonObject CreateDocument(string title, JsonObject paths)
    {
        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = title,
                ["version"] = CourseContracts.Version,
            },
            ["paths"] = paths,
        };
    }

    private static JsonObject CreatePathItem(ActionManifest manifest)
    {
        JsonNode requestSchema = JsonNode.Parse(manifest.RequestSchema.GetRawText()) ?? new JsonObject();
        JsonNode responseSchema = JsonNode.Parse(manifest.ResponseSchema.GetRawText()) ?? new JsonObject();
        return new JsonObject
        {
            ["post"] = new JsonObject
            {
                ["operationId"] = $"{manifest.Module}.{manifest.Action}.{manifest.Version}",
                ["parameters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "X-Action-Version",
                        ["in"] = "header",
                        ["required"] = false,
                        ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    },
                    new JsonObject
                    {
                        ["name"] = "Idempotency-Key",
                        ["in"] = "header",
                        ["required"] = string.Equals(manifest.IdempotencyMode, "required", StringComparison.Ordinal),
                        ["schema"] = new JsonObject { ["type"] = "string" },
                    },
                },
                ["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = requestSchema },
                    },
                },
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject
                    {
                        ["description"] = "Action succeeded",
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject { ["schema"] = responseSchema },
                        },
                    },
                },
            },
        };
    }
}
