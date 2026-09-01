using System.Text.Json;
using ReliaPay.Contracts;
using ReliaPay.Json;

namespace ReliaPay.Catalog;

/// <summary>
/// Читает JSON или YAML карту, проверяет schema курса и собирает типизированную модель.
/// </summary>
public sealed class WorkflowMapReader
{
    private readonly JsonDocumentValidator _validator;

    /// <summary>
    /// Создаёт читателя с валидатором JSON Schema.
    /// </summary>
    public WorkflowMapReader(JsonDocumentValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <summary>
    /// Разбирает текст карты. YAML определяется по расширению или если JSON не парсится.
    /// </summary>
    /// <param name="text">Содержимое файла или stdin.</param>
    /// <param name="pathHint">Путь файла; <c>.yaml</c>/<c>.yml</c> форсируют YAML.</param>
    public WorkflowMapReadResult Read(string text, string? pathHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!TryNormalizeJson(text, pathHint, out string json, out string? convertError))
        {
            return Fail(convertError ?? "map is not valid JSON or YAML");
        }

        SchemaValidationResult validation = _validator.Validate(json, CourseJsonSchemas.WorkflowMap);
        if (!validation.IsValid)
        {
            return Fail(validation.ErrorMessage ?? "map does not match schema");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            List<WorkflowMapStep> steps = [];
            foreach (JsonElement step in root.GetProperty("steps").EnumerateArray())
            {
                steps.Add(ReadStep(step));
            }

            List<WorkflowMapTransition> transitions = [];
            foreach (JsonElement transition in root.GetProperty("transitions").EnumerateArray())
            {
                transitions.Add(
                    new WorkflowMapTransition(
                        transition.GetProperty("from").GetString() ?? string.Empty,
                        transition.GetProperty("outcome").GetString() ?? string.Empty,
                        transition.GetProperty("to").GetString() ?? string.Empty));
            }

            WorkflowMap map = new(
                FlowName: root.GetProperty("flow_name").GetString() ?? string.Empty,
                Version: root.GetProperty("version").GetInt32(),
                StartStep: root.GetProperty("start_step").GetString() ?? string.Empty,
                Steps: steps,
                Transitions: transitions,
                CanonicalJson: json);
            return new WorkflowMapReadResult(map, null, null);
        }
        catch (JsonException)
        {
            return Fail("map is not valid JSON");
        }
    }

    /// <summary>
    /// Переводит YAML в JSON либо оставляет JSON как есть.
    /// </summary>
    internal static bool TryNormalizeJson(string text, string? pathHint, out string json, out string? error)
    {
        json = text;
        error = null;
        bool forceYaml = pathHint is not null
            && (pathHint.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || pathHint.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
        if (!forceYaml)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    json = text;
                    return true;
                }
            }
            catch (JsonException)
            {
                // YAML below.
            }
        }

        return YamlJsonConverter.TryConvert(text, out json, out error);
    }

    /// <summary>
    /// Собирает шаг одного из четырёх типов карты.
    /// </summary>
    private static WorkflowMapStep ReadStep(JsonElement step)
    {
        string type = step.GetProperty("type").GetString() ?? string.Empty;
        string key = step.GetProperty("key").GetString() ?? string.Empty;
        if (type == "automatic")
        {
            JsonElement task = step.GetProperty("task");
            Dictionary<string, string> mapping = new(StringComparer.Ordinal);
            foreach (JsonProperty property in task.GetProperty("input_mapping").EnumerateObject())
            {
                mapping[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            List<int> delays = [];
            foreach (JsonElement delay in task.GetProperty("retry").GetProperty("delays_ms").EnumerateArray())
            {
                delays.Add(delay.GetInt32());
            }

            return new WorkflowMapStep(
                key,
                type,
                new WorkflowMapTask(
                    task.GetProperty("module").GetString() ?? string.Empty,
                    task.GetProperty("action").GetString() ?? string.Empty,
                    task.GetProperty("action_version").GetInt32(),
                    ReadStringArray(task.GetProperty("required_policy")),
                    task.GetProperty("timeout_ms").GetInt32(),
                    task.GetProperty("retry").GetProperty("max_attempts").GetInt32(),
                    delays,
                    mapping,
                    task.GetProperty("input_constants").Clone()),
                null,
                null,
                null);
        }

        if (type == "wait_signal")
        {
            return new WorkflowMapStep(
                key,
                type,
                null,
                step.GetProperty("signal_type").GetString(),
                step.GetProperty("outcome").GetString(),
                null);
        }

        if (type == "manual")
        {
            return new WorkflowMapStep(
                key,
                type,
                null,
                null,
                null,
                ReadStringArray(step.GetProperty("allowed_outcomes")));
        }

        return new WorkflowMapStep(
            key,
            type,
            null,
            null,
            step.GetProperty("outcome").GetString(),
            null);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element)
    {
        List<string> values = [];
        foreach (JsonElement item in element.EnumerateArray())
        {
            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    private static WorkflowMapReadResult Fail(string message)
    {
        return new WorkflowMapReadResult(null, ErrorCodes.RequestInvalid, message);
    }
}
