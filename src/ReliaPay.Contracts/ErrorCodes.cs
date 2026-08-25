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
}
