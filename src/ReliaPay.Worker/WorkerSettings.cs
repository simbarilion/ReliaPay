namespace ReliaPay.Worker;

/// <summary>
/// Параметры generic worker из окружения Compose.
/// </summary>
/// <param name="InstanceId">Владелец lease: <c>worker-a</c> или <c>worker-b</c>.</param>
/// <param name="PollInterval">Пауза при пустом claim. В тестовом профиле не больше 100 мс.</param>
/// <param name="LeaseMs">Длительность lease при claim; в тестовом профиле 2000.</param>
/// <param name="Failpoint">Имя COURSE_FAILPOINT либо null. Arm только при COURSE_TEST_PROFILE=1.</param>
public sealed record WorkerSettings(
    string InstanceId,
    TimeSpan PollInterval,
    int LeaseMs,
    string? Failpoint)
{
    /// <summary>
    /// Читает <c>COURSE_WORKER_INSTANCE_ID</c>, <c>COURSE_TEST_PROFILE</c> и <c>COURSE_FAILPOINT</c>.
    /// </summary>
    public static WorkerSettings FromEnvironment()
    {
        return Create(
            Environment.GetEnvironmentVariable("COURSE_WORKER_INSTANCE_ID"),
            Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE"),
            Environment.GetEnvironmentVariable("COURSE_FAILPOINT"));
    }

    /// <summary>
    /// Собирает настройки без чтения окружения — для тестов.
    /// </summary>
    /// <param name="instanceId">Владелец lease.</param>
    /// <param name="testProfile">Значение <c>COURSE_TEST_PROFILE</c>; <c>1</c> включает короткий poll и failpoint.</param>
    /// <param name="failpoint">Имя failpoint либо пусто.</param>
    public static WorkerSettings Create(string? instanceId, string? testProfile, string? failpoint)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new InvalidOperationException("Environment variable COURSE_WORKER_INSTANCE_ID is required.");
        }

        bool test = string.Equals(testProfile?.Trim(), "1", StringComparison.Ordinal);
        string? name = test && !string.IsNullOrWhiteSpace(failpoint) ? failpoint.Trim() : null;
        return new WorkerSettings(
            instanceId.Trim(),
            test ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(500),
            2000,
            name);
    }
}