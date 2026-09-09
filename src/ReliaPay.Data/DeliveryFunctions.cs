namespace ReliaPay.Data;

/// <summary>
/// Фиксированные SQL-границы Python-периметра. Роли integration получают EXECUTE
/// только на перечисленные функции схемы <c>delivery</c>, без table DML.
/// </summary>
public static class DeliveryFunctions
{
    /// <summary>Захват PENDING/RETRY_WAIT/истёкшего LEASED: одна строка — одна HTTP-попытка.</summary>
    public const string ClaimOutbox = "claim_outbox";

    /// <summary>Условный успех доставки; не регрессирует CONFIRMED.</summary>
    public const string SucceedOutbox = "succeed_outbox";

    /// <summary>Условный fail: RETRY_WAIT по retryable-коду или терминальный DEAD.</summary>
    public const string FailOutbox = "fail_outbox";

    /// <summary>Применение RECEIVED Inbox к pinned process; возвращает число APPLIED.</summary>
    public const string ReconcileInbox = "reconcile_inbox";

    /// <summary>Единственные функции, на которые <c>outbox_dispatcher</c> получает EXECUTE.</summary>
    public static readonly IReadOnlyList<string> OutboxDispatcherExecute =
    [
        ClaimOutbox,
        SucceedOutbox,
        FailOutbox
    ];

    /// <summary>Единственная функция EXECUTE для <c>inbox_reconciler</c>.</summary>
    public static readonly IReadOnlyList<string> InboxReconcilerExecute =
    [
        ReconcileInbox
    ];
}
