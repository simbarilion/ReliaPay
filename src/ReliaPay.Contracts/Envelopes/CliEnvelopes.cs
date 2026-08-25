namespace ReliaPay.Contracts.Envelopes;

/// <summary>
/// Метаданные единственного JSON-документа, который CLI пишет в stdout.
/// </summary>
/// <param name="ContractVersion">Версия контракта курса, для недели 1 всегда <c>course-1</c>.</param>
public sealed record CliMeta(string ContractVersion);

/// <summary>
/// Успешный результат CLI-команды.
/// </summary>
/// <param name="Status">Всегда <c>ok</c>.</param>
/// <param name="Result">Описание затронутого ресурса и операции.</param>
/// <param name="Meta">Метаданные контракта.</param>
public sealed record CliSuccessEnvelope(string Status, CliSuccessResult Result, CliMeta Meta);

/// <summary>
/// Поле <c>result</c> успешного CLI-ответа.
/// </summary>
/// <param name="Resource">Тип ресурса, например <c>action</c> или <c>migration</c>.</param>
/// <param name="Operation">Выполненная операция, например <c>published</c>.</param>
/// <param name="Key">Ключ ресурса, например <c>payment.request</c>.</param>
/// <param name="Version">Версия action, если применима.</param>
public sealed record CliSuccessResult(string Resource, string Operation, string Key, int? Version);

/// <summary>
/// Ошибочный CLI-конверт. Пишется в stdout; диагностика допускается только в stderr.
/// </summary>
/// <param name="Status">Всегда <c>error</c>.</param>
/// <param name="Code">Нормативный или стабильный код ошибки CLI.</param>
/// <param name="Message">Краткое сообщение без секретов.</param>
/// <param name="Meta">Метаданные контракта.</param>
public sealed record CliErrorEnvelope(string Status, string Code, string Message, CliMeta Meta)
{
    /// <summary>
    /// Собирает ошибочный конверт с версией контракта курса.
    /// </summary>
    /// <param name="code">Код ошибки.</param>
    /// <param name="message">Сообщение оператору CLI.</param>
    /// <returns>Конверт для записи в stdout.</returns>
    public static CliErrorEnvelope Create(string code, string message)
    {
        return new CliErrorEnvelope(
            Status: "error",
            Code: code,
            Message: message,
            Meta: new CliMeta(CourseContracts.Version));
    }
}
