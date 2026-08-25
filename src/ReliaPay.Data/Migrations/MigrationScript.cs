namespace ReliaPay.Data.Migrations;

/// <summary>
/// SQL-файл, который CLI применяет ровно в одной транзакции.
/// </summary>
/// <param name="FileName">Имя файла без пути, ключ журнала <c>api.schema_migrations</c>.</param>
/// <param name="FullPath">Абсолютный путь для чтения байт.</param>
/// <param name="Checksum">SHA-256 lowercase hex содержимого.</param>
/// <param name="Sql">Текст файла в UTF-8.</param>
public sealed record MigrationScript(string FileName, string FullPath, string Checksum, string Sql);
