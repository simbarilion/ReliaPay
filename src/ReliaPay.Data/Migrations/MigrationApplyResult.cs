namespace ReliaPay.Data.Migrations;

/// <summary>
/// Итог <c>migration apply</c>: сколько файлов выполнено и сколько пропущено как уже применённые.
/// </summary>
/// <param name="DirectoryPath">Каталог, который передал оператор.</param>
/// <param name="AppliedCount">Число файлов, для которых выполнена транзакция.</param>
/// <param name="SkippedCount">Число файлов с совпавшим checksum.</param>
public sealed record MigrationApplyResult(string DirectoryPath, int AppliedCount, int SkippedCount);
