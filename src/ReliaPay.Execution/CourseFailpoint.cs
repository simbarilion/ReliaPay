using System.Text.Json;

namespace ReliaPay.Execution;

/// <summary>
/// Учебный failpoint для HTTP-runtime: одна JSON-строка в stdout, затем бесконечное ожидание.
/// Arm только при <c>COURSE_TEST_PROFILE=1</c> и совпавшем <c>COURSE_FAILPOINT</c>.
/// </summary>
public sealed class CourseFailpoint
{
    /// <summary>После commit Inbox/receipt/idempotency, до HTTP-ответа.</summary>
    public const string AfterInboxSaved = "after_inbox_saved";

    /// <summary>После успешного <c>workflow.manual</c> effect, до commit общей транзакции.</summary>
    public const string AfterManualDecision = "after_manual_decision";

    /// <summary>Экземпляр, который никогда не срабатывает.</summary>
    public static CourseFailpoint Disabled { get; } = new(configured: null, testProfileEnabled: false, instanceId: "api", output: TextWriter.Null);

    private readonly string? _configured;
    private readonly bool _enabled;
    private readonly string _instanceId;
    private readonly TextWriter _output;

    /// <summary>
    /// Создаёт фильтр по имени failpoint.
    /// </summary>
    /// <param name="configured">Значение <c>COURSE_FAILPOINT</c>.</param>
    /// <param name="testProfileEnabled"><c>true</c>, если <c>COURSE_TEST_PROFILE=1</c>.</param>
    /// <param name="instanceId">Непустой идентификатор экземпляра в ack.</param>
    /// <param name="output">Поток ack; для процесса это <see cref="Console.Out"/>.</param>
    public CourseFailpoint(string? configured, bool testProfileEnabled, string instanceId, TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(output);
        _enabled = testProfileEnabled;
        _configured = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        _instanceId = instanceId.Trim();
        _output = output;
    }

    /// <summary>
    /// Читает окружение Compose для API.
    /// </summary>
    /// <param name="output">Поток ack.</param>
    public static CourseFailpoint FromEnvironment(TextWriter? output = null)
    {
        bool test = string.Equals(
            Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE")?.Trim(),
            "1",
            StringComparison.Ordinal);
        string? name = Environment.GetEnvironmentVariable("COURSE_FAILPOINT");
        string? instanceId = Environment.GetEnvironmentVariable("COURSE_API_INSTANCE_ID");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            instanceId = "api";
        }

        return new CourseFailpoint(name, test, instanceId, output ?? Console.Out);
    }

    /// <summary>
    /// Если имя совпало и test profile включён, пишет ack и зависает до отмены.
    /// </summary>
    /// <param name="name">Имя точки границы.</param>
    /// <param name="cancellationToken">Остановка контейнера / HTTP abort.</param>
    public async Task HitAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_enabled || !string.Equals(_configured, name, StringComparison.Ordinal))
        {
            return;
        }

        string line = JsonSerializer.Serialize(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["event"] = "failpoint.reached",
                ["name"] = name,
                ["instanceId"] = _instanceId,
            });
        await _output.WriteLineAsync(line).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}