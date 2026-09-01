namespace ReliaPay.Data;

/// <summary>
/// Имена SECURITY DEFINER-функций схемы <c>workflow</c>. Worker получает EXECUTE
/// только на claim/finish/fail; публикация карт и процессов идёт ролью CLI.
/// </summary>
public static class WorkflowFunctions
{
    /// <summary>Захват READY/RETRY_WAIT/истёкшего LEASED job с FOR UPDATE SKIP LOCKED.</summary>
    public const string ClaimJobs = "claim_jobs";

    /// <summary>Успешное завершение job с fencing по owner и leaseVersion.</summary>
    public const string FinishJob = "finish_job";

    /// <summary>Ошибка job: RETRY_WAIT по policy или терминальный DEAD.</summary>
    public const string FailJob = "fail_job";

    /// <summary>Публикация неизменяемой версии карты.</summary>
    public const string PublishFlow = "publish_flow";

    /// <summary>Единственная активная версия flow_name.</summary>
    public const string ActivateFlow = "activate_flow";

    /// <summary>Идемпотентный старт process по (flow_name, business_key).</summary>
    public const string StartProcess = "start_process";

    /// <summary>Идемпотентный сигнал с глобально уникальным message_id.</summary>
    public const string SignalProcess = "signal_process";

    /// <summary>Чтение process для CLI <c>flow get</c>.</summary>
    public const string GetProcess = "get_process";

    /// <summary>Список опубликованных версий карт.</summary>
    public const string ListFlows = "list_flows";

    /// <summary>
    /// Единственные функции схемы <c>workflow</c>, на которые worker получает EXECUTE.
    /// </summary>
    public static readonly IReadOnlyList<string> WorkerExecute =
    [
        ClaimJobs,
        FinishJob,
        FailJob
    ];
}
