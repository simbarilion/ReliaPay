namespace ReliaPay.Contracts;

/// <summary>
/// Идентификатор опубликованного контракта курса. Все CLI- и HTTP-конверты
/// обязаны указывать это значение в <c>meta.contractVersion</c> там, где поле требуется заданием.
/// </summary>
public static class CourseContracts
{
    /// <summary>
    /// Каноническая версия machine-readable контрактов недели 1.
    /// </summary>
    public const string Version = "course-1";
}
