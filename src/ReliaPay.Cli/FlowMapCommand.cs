using System.Text.Json;
using System.Text.Json.Nodes;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Data;
using ReliaPay.Json;

namespace ReliaPay.Cli;

/// <summary>
/// Печатает jsonb-конверт SECURITY DEFINER как единственный CLI JSON.
/// </summary>
internal static class WorkflowEnvelopeWriter
{
    /// <summary>
    /// Если PostgreSQL вернул <c>status=ok</c>, поле result (или остальные поля) уходит в CLI-конверт.
    /// </summary>
    public static async Task<int> WriteAsync(string json, IReadOnlyDictionary<string, string>? extra = null)
    {
        json = WorkflowCliResult.ApplyContractFields(json, extra);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string status = root.TryGetProperty("status", out JsonElement statusElement)
            ? statusElement.GetString() ?? "error"
            : "error";
        if (!string.Equals(status, "ok", StringComparison.Ordinal))
        {
            return await CliFailure.WriteAsync(
                    root.TryGetProperty("code", out JsonElement code) ? code.GetString() ?? ErrorCodes.InternalError : ErrorCodes.InternalError,
                    root.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? "workflow command failed" : "workflow command failed")
                .ConfigureAwait(false);
        }

        JsonNode result;
        if (root.TryGetProperty("result", out JsonElement nested))
        {
            result = JsonNode.Parse(nested.GetRawText()) ?? new JsonObject();
        }
        else
        {
            JsonObject obj = [];
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.NameEquals("status"))
                {
                    continue;
                }

                obj[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }

            result = obj;
        }

        JsonObject envelope = new()
        {
            ["status"] = "ok",
            ["result"] = result,
            ["meta"] = new JsonObject { ["contractVersion"] = CourseContracts.Version },
        };
        await Console.Out.WriteLineAsync(envelope.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        })).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
        return 0;
    }
}

/// <summary>
/// Команды публикации карт: validate и publish.
/// </summary>
internal static class FlowMapCommand
{
    /// <summary>
    /// Schema и семантика без записи.
    /// </summary>
    public static Task<int> ValidateAsync(string mapPath, CancellationToken cancellationToken)
    {
        return RunAsync(mapPath, publish: false, cancellationToken);
    }

    /// <summary>
    /// Валидация и <c>workflow.publish_flow</c>.
    /// </summary>
    public static Task<int> PublishAsync(string mapPath, CancellationToken cancellationToken)
    {
        return RunAsync(mapPath, publish: true, cancellationToken);
    }

    private static async Task<int> RunAsync(string mapPath, bool publish, CancellationToken cancellationToken)
    {
        if (!CliInput.Exists(mapPath))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "map file does not exist").ConfigureAwait(false);
        }

        string text = await CliInput.ReadAsync(mapPath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "map is empty").ConfigureAwait(false);
        }

        WorkflowMapReader reader = new(new JsonDocumentValidator());
        WorkflowMapReadResult read = reader.Read(text, mapPath);
        if (!read.IsSuccess || read.Map is null)
        {
            return await CliFailure.WriteAsync(
                    read.ErrorCode ?? ErrorCodes.RequestInvalid,
                    read.ErrorMessage ?? "map is invalid")
                .ConfigureAwait(false);
        }

        try
        {
            WorkflowClient client = CreateClient();
            (string Code, string Message)? semantic = await new WorkflowMapSemanticValidator()
                .ValidateAsync(read.Map, client, cancellationToken)
                .ConfigureAwait(false);
            if (semantic is not null)
            {
                return await CliFailure.WriteAsync(semantic.Value.Code, semantic.Value.Message).ConfigureAwait(false);
            }

            if (!publish)
            {
                await CliStdout.WriteAsync(
                        new CliSuccessEnvelope(
                            "ok",
                            new CliSuccessResult("flow", "validated", read.Map.FlowName, read.Map.Version),
                            new CliMeta(CourseContracts.Version)))
                    .ConfigureAwait(false);
                return 0;
            }

            string json = await client.PublishAsync(read.Map.CanonicalJson, cancellationToken).ConfigureAwait(false);
            return await WorkflowEnvelopeWriter.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Клиент publication-роли.
    /// </summary>
    internal static WorkflowClient CreateClient()
    {
        return new WorkflowClient(new PostgresConnectionFactory(PostgresEndpoints.FromEnvironment()));
    }
}
