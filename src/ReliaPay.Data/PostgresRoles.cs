namespace ReliaPay.Data;

/// <summary>
/// Имена прикладных ролей PostgreSQL. Superuser не входит в модель угроз задания.
/// </summary>
public static class PostgresRoles
{
    /// <summary>
    /// Владелец SECURITY DEFINER объектов; роль без права входа.
    /// </summary>
    public const string Owner = "course_owner";

    /// <summary>
    /// Роль CLI: миграции и публикация каталога. API её не использует.
    /// </summary>
    public const string Publication = "course_publication";

    /// <summary>
    /// Роль runtime: только <c>api.invoke</c> и чтение autocheck, без прямого DML в предмет.
    /// </summary>
    public const string Runtime = "course_runtime";

    /// <summary>
    /// Роль generic worker: только <c>workflow.claim_jobs</c>, <c>api.invoke</c>,
    /// <c>workflow.finish_job</c> и <c>workflow.fail_job</c>. Прямого DML нет.
    /// </summary>
    public const string Worker = "workflow_worker";

    /// <summary>
    /// Python outbox-dispatcher: только <c>delivery.claim_outbox</c>,
    /// <c>delivery.succeed_outbox</c> и <c>delivery.fail_outbox</c>.
    /// </summary>
    public const string OutboxDispatcher = "outbox_dispatcher";

    /// <summary>
    /// Python inbox-reconciler: только <c>delivery.reconcile_inbox</c>.
    /// </summary>
    public const string InboxReconciler = "inbox_reconciler";
}
