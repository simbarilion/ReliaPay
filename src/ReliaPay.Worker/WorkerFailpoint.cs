using System.Text.Json;

namespace ReliaPay.Worker;

/// <summary>
/// Учебный failpoint: одна JSON-строка в stdout, затем бесконечное ожидание.
/// </summary>
public sealed class WorkerFailpoint
{
    /// <summary>После успешного claim и до <c>api.invoke</c>.</summary>
    public const string AfterJobClaim = "after_job_claim";

    /// <summary>После успешного invoke и до <c>finish_job</c>, на открытой транзакции.</summary>
    public const string AfterActionBeforeFinish = "after_action_before_finish";

    private readonly string? _configured;
    private readonly TextWriter _output;

    /// <summary>
    /// Создаёт фильтр по имени <c>COURSE_FAILPOINT</c>.
    /// </summary>
    /// <param name="configured">Настроенное имя или null.</param>
    /// <param name="output">Поток ack; для процесса это <see cref="Console.Out"/>.</param>
    public WorkerFailpoint(string? configured, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _configured = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        _output = output;
    }

    /// <summary>
    /// Если имя совпало, пишет ack и зависает, пока токен не отменят.
    /// </summary>
    /// <param name="name">Имя точки в цикле job.</param>
    /// <param name="instanceId">Владелец lease, обычно <c>worker-a</c> или <c>worker-b</c>.</param>
    /// <param name="cancellationToken">Остановка контейнера.</param>
    public async Task HitAsync(string name, string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (!string.Equals(_configured, name, StringComparison.Ordinal))
        {
            return;
        }

        string line = JsonSerializer.Serialize(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["event"] = "failpoint.reached",
                ["name"] = name,
                ["instanceId"] = instanceId,
            });
        await _output.WriteLineAsync(line).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}
