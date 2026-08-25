namespace ReliaPay.Data.Migrations;

/// <summary>
/// Файл миграции уже применён с другим содержимым. Повтор безопасен только при том же checksum.
/// </summary>
public sealed class MigrationConflictException : InvalidOperationException
{
    /// <summary>
    /// Создаёт ошибку неизменяемости уже применённого файла.
    /// </summary>
    /// <param name="fileName">Имя файла.</param>
    public MigrationConflictException(string fileName)
        : base($"applied migration file is immutable: {fileName}")
    {
        FileName = fileName;
    }

    /// <summary>Имя конфликтующего файла.</summary>
    public string FileName { get; }
}
