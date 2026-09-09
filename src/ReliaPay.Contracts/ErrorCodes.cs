namespace ReliaPay.Contracts;

/// <summary>
/// Нормативные коды ошибок HTTP- и CLI-контуров. Строки совпадают с матрицей ModuleDev course-1
/// и не должны подменяться сообщениями фреймворка.
/// </summary>
public static class ErrorCodes
{
    /// <summary>
    /// Неверный, отсутствующий, просроченный JWT либо claim неверного JSON-типа.
    /// </summary>
    public const string AuthInvalid = "auth.invalid";

    /// <summary>
    /// Формат, версия или HMAC <c>X-Provider-Signature</c> неверны. Target не вызывается.
    /// </summary>
    public const string SignatureInvalid = "signature.invalid";

    /// <summary>
    /// <c>receipt.accept</c> вызван без проверенной transport-подписи.
    /// </summary>
    public const string ReceiptSignatureRequired = "receipt.signature_required";

    /// <summary>
    /// В payload receipt нет известного <c>externalRequestId</c>.
    /// </summary>
    public const string ReceiptExternalRequestNotFound = "receipt.external_request_not_found";

    /// <summary>
    /// Невалидный JSON тела, маршрут или заголовок версии.
    /// </summary>
    public const string RequestInvalid = "request.invalid";

    /// <summary>
    /// Для action с <c>idempotency_mode=required</c> не передан Idempotency-Key.
    /// </summary>
    public const string IdempotencyRequired = "idempotency.required";

    /// <summary>
    /// В доверенном контексте нет всех scopes из required_policy манифеста.
    /// </summary>
    public const string AccessDenied = "access.denied";

    /// <summary>
    /// Action или версия неизвестны, выключены либо не резолвятся из каталога.
    /// </summary>
    public const string ActionNotFound = "action.not_found";

    /// <summary>
    /// Тот же ключ идемпотентности передан с другим payload.
    /// </summary>
    public const string IdempotencyConflict = "idempotency.conflict";

    /// <summary>
    /// Payload не соответствует request_schema манифеста.
    /// </summary>
    public const string PayloadInvalid = "payload.invalid";

    /// <summary>
    /// PostgreSQL или иная обязательная зависимость временно недоступна.
    /// </summary>
    public const string DependencyUnavailable = "dependency.unavailable";

    /// <summary>
    /// Outcome или result нарушают контракт манифеста после вызова target.
    /// </summary>
    public const string ActionContractViolation = "action.contract_violation";

    /// <summary>
    /// Истёк timeout_ms манифеста.
    /// </summary>
    public const string ActionTimeout = "action.timeout";

    /// <summary>
    /// Необработанная ошибка без раскрытия внутренних деталей.
    /// </summary>
    public const string InternalError = "internal.error";

    /// <summary>
    /// Манифест не является JSON, не проходит schema или нарушает правила каталога.
    /// </summary>
    public const string ManifestInvalid = "manifest.invalid";

    /// <summary>
    /// Опубликованная версия манифеста неизменяема, повтор с другим телом запрещён.
    /// </summary>
    public const string ManifestConflict = "manifest.conflict";

    /// <summary>
    /// Предметная операция с указанным идентификатором не найдена.
    /// </summary>
    public const string OperationNotFound = "operation.not_found";

    /// <summary>
    /// Файл миграции уже применялся с другим checksum.
    /// </summary>
    public const string MigrationConflict = "migration.conflict";

    /// <summary>
    /// Каталог миграций отсутствует либо не читается.
    /// </summary>
    public const string MigrationInvalid = "migration.invalid";

    /// <summary>
    /// Источник JSON Pointer в process data отсутствует; action не вызывается.
    /// </summary>
    public const string WorkflowMappingMissing = "workflow.mapping_missing";

    /// <summary>
    /// Finish с чужим owner или устаревшим leaseVersion отклонён без изменения состояния.
    /// </summary>
    public const string WorkflowLeaseStale = "workflow.lease_stale";

    /// <summary>
    /// Конфликт идемпотентности процесса, сигнала или неизменяемой версии карты.
    /// </summary>
    public const string WorkflowConflict = "workflow.conflict";
}
