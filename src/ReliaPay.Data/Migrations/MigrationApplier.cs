namespace ReliaPay.Data.Migrations;

/// <summary>
/// Применяет SQL-файлы по одному, сверяя SHA-256 с журналом PostgreSQL.
/// </summary>
public sealed class MigrationApplier
{
    private readonly IMigrationJournal _journal;

    /// <summary>
    /// Создаёт апплаер поверх журнала (боевой Npgsql или подделка в тестах).
    /// </summary>
    /// <param name="journal">Хранилище checksum.</param>
    public MigrationApplier(IMigrationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _journal = journal;
    }

    /// <summary>
    /// Обрабатывает каталог: пропуск при том же checksum, conflict при другом, иначе execute+commit.
    /// </summary>
    /// <param name="directory">Путь, доступный внутри контейнера CLI.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сводка apply.</returns>
    /// <exception cref="DirectoryNotFoundException">Каталог не найден.</exception>
    /// <exception cref="MigrationConflictException">Файл изменили после apply.</exception>
    public async Task<MigrationApplyResult> ApplyAsync(string directory, CancellationToken cancellationToken)
    {
        IReadOnlyList<MigrationScript> scripts = MigrationScriptLoader.Load(directory);
        int applied = 0;
        int skipped = 0;
        foreach (MigrationScript script in scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? existing = await _journal.GetChecksumAsync(script.FileName, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await _journal.ApplyInTransactionAsync(script, cancellationToken).ConfigureAwait(false);
                applied++;
                continue;
            }

            if (string.Equals(existing, script.Checksum, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            throw new MigrationConflictException(script.FileName);
        }

        return new MigrationApplyResult(directory, applied, skipped);
    }
}
