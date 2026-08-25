namespace ReliaPay.Data.Migrations;

/// <summary>
/// Перечисляет <c>*.sql</c> верхнего уровня каталога в лексикографическом порядке имён.
/// </summary>
public static class MigrationScriptLoader
{
    /// <summary>
    /// Читает и хеширует все SQL-файлы каталога.
    /// </summary>
    /// <param name="directory">Каталог, доступный процессу CLI.</param>
    /// <returns>Скрипты по возрастанию имени файла.</returns>
    /// <exception cref="DirectoryNotFoundException">Каталог не существует.</exception>
    public static IReadOnlyList<MigrationScript> Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        string[] files = Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        List<MigrationScript> scripts = new(files.Length);
        foreach (string fullPath in files)
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            string checksum = MigrationChecksum.ComputeSha256Hex(bytes);
            string sql = System.Text.Encoding.UTF8.GetString(bytes);
            scripts.Add(new MigrationScript(Path.GetFileName(fullPath), fullPath, checksum, sql));
        }

        return scripts;
    }
}
