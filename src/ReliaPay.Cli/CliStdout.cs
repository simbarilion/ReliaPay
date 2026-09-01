using System.Text.Json;
using ReliaPay.Contracts.Envelopes;

namespace ReliaPay.Cli;

/// <summary>
/// Печатает ровно один JSON-документ в stdout. Любая диагностика уходит в stderr.
/// </summary>
public static class CliStdout
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Сериализует успешный конверт в stdout.
    /// </summary>
    /// <param name="envelope">Контрактный успех CLI.</param>
    /// <returns>Задача записи.</returns>
    public static Task WriteAsync(CliSuccessEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Console.Out.WriteLine(JsonSerializer.Serialize(envelope, Options));
        Console.Out.Flush();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Сериализует ошибочный конверт в stdout.
    /// </summary>
    /// <param name="envelope">Контрактная ошибка CLI.</param>
    /// <returns>Задача записи.</returns>
    public static Task WriteAsync(CliErrorEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Console.Out.WriteLine(JsonSerializer.Serialize(envelope, Options));
        Console.Out.Flush();
        return Task.CompletedTask;
    }
}
