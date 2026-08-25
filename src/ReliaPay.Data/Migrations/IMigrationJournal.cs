namespace ReliaPay.Data.Migrations;

/// <summary>
/// Журнал уже применённых миграций. Реализация для PostgreSQL пишет в <c>api.schema_migrations</c>.
/// </summary>
public interface IMigrationJournal
{
    /// <summary>
    /// Возвращает сохранённый checksum файла либо null, если файл ещё не применялся.
    /// </summary>
    /// <param name="fileName">Имя файла миграции.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Checksum или null.</returns>
    Task<string?> GetChecksumAsync(string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// В одной транзакции выполняет SQL от имени <c>course_owner</c> и записывает checksum.
    /// </summary>
    /// <param name="script">Файл миграции.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача завершения транзакции.</returns>
    Task ApplyInTransactionAsync(MigrationScript script, CancellationToken cancellationToken);
}
