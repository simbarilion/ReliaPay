namespace ReliaPay.Contracts.Operations;

/// <summary>
/// Допустимые виды операции payment.request. Клиент не передаёт workflow и финальный status.
/// </summary>
public static class OperationKinds
{
    /// <summary>Исполнение платежа.</summary>
    public const string PaymentExecution = "PAYMENT_EXECUTION";

    /// <summary>Согласование платежа.</summary>
    public const string PaymentApproval = "PAYMENT_APPROVAL";
}

/// <summary>
/// Предметные статусы operation. На неделе 1 создаётся только <see cref="Created"/>.
/// </summary>
public static class OperationStatuses
{
    /// <summary>Операция принята и зафиксирована.</summary>
    public const string Created = "CREATED";

    /// <summary>Операция в обработке процессом. На неделе 1 не используется.</summary>
    public const string Processing = "PROCESSING";

    /// <summary>Операция завершена успешно. На неделе 1 не используется.</summary>
    public const string Completed = "COMPLETED";

    /// <summary>Операция отклонена. На неделе 1 не используется.</summary>
    public const string Rejected = "REJECTED";
}

/// <summary>
/// Контрактный result для payment.request и operation.get.
/// </summary>
/// <param name="OperationId">Идентификатор операции, назначенный сервером.</param>
/// <param name="RequestId">Ключ идемпотентности, сохранённый при создании.</param>
/// <param name="OperationKind">PAYMENT_EXECUTION или PAYMENT_APPROVAL.</param>
/// <param name="Amount">Исходная десятичная строка суммы.</param>
/// <param name="Currency">Валюта, на неделе 1 только RUB.</param>
/// <param name="Status">Статус operation, при создании CREATED.</param>
public sealed record OperationResult(
    Guid OperationId,
    string RequestId,
    string OperationKind,
    string Amount,
    string Currency,
    string Status);
