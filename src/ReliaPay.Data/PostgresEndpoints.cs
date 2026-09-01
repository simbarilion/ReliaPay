namespace ReliaPay.Data;

/// <summary>
/// Параметры подключения к PostgreSQL по прикладным ролям.
/// Пароль не включается в <see cref="ToString"/>, чтобы не попасть в логи.
/// </summary>
public sealed class PostgresEndpoints
{
    /// <summary>
    /// Создаёт набор параметров без логирования секретов.
    /// </summary>
    /// <param name="host">Имя хоста, в Compose это <c>postgres</c>.</param>
    /// <param name="port">Порт PostgreSQL.</param>
    /// <param name="database">Имя базы, для курса <c>course</c>.</param>
    /// <param name="publicationUser">Роль CLI.</param>
    /// <param name="publicationPassword">Пароль роли CLI.</param>
    /// <param name="runtimeUser">Роль HTTP runtime.</param>
    /// <param name="runtimePassword">Пароль роли runtime.</param>
    /// <param name="workerUser">Роль generic worker.</param>
    /// <param name="workerPassword">Пароль роли worker.</param>
    public PostgresEndpoints(
        string host,
        int port,
        string database,
        string publicationUser,
        string publicationPassword,
        string runtimeUser,
        string runtimePassword,
        string? workerUser = null,
        string? workerPassword = null)
    {
        Host = host;
        Port = port;
        Database = database;
        PublicationUser = publicationUser;
        PublicationPassword = publicationPassword;
        RuntimeUser = runtimeUser;
        RuntimePassword = runtimePassword;
        WorkerUser = string.IsNullOrWhiteSpace(workerUser) ? PostgresRoles.Worker : workerUser;
        WorkerPassword = workerPassword ?? string.Empty;
    }

    /// <summary>Имя или адрес сервера PostgreSQL.</summary>
    public string Host { get; }

    /// <summary>TCP-порт PostgreSQL.</summary>
    public int Port { get; }

    /// <summary>Имя базы данных.</summary>
    public string Database { get; }

    /// <summary>Имя роли publication.</summary>
    public string PublicationUser { get; }

    /// <summary>Пароль роли publication. Не логировать.</summary>
    public string PublicationPassword { get; }

    /// <summary>Имя роли runtime.</summary>
    public string RuntimeUser { get; }

    /// <summary>Пароль роли runtime. Не логировать.</summary>
    public string RuntimePassword { get; }

    /// <summary>Имя роли worker.</summary>
    public string WorkerUser { get; }

    /// <summary>Пароль роли worker. Не логировать.</summary>
    public string WorkerPassword { get; }

    /// <summary>
    /// Читает параметры CLI: host и обе прикладные роли.
    /// </summary>
    /// <returns>Типизированные параметры подключения.</returns>
    /// <exception cref="InvalidOperationException">Обязательная переменная не задана или порт нечисловой.</exception>
    public static PostgresEndpoints FromEnvironment()
    {
        return new PostgresEndpoints(
            host: ReadRequired("COURSE_DB_HOST"),
            port: ReadPort("COURSE_DB_PORT"),
            database: ReadRequired("COURSE_DB_NAME"),
            publicationUser: ReadRequired("COURSE_PUBLICATION_USER"),
            publicationPassword: ReadRequired("COURSE_PUBLICATION_PASSWORD"),
            runtimeUser: Environment.GetEnvironmentVariable("COURSE_RUNTIME_USER") ?? PostgresRoles.Runtime,
            runtimePassword: Environment.GetEnvironmentVariable("COURSE_RUNTIME_PASSWORD") ?? string.Empty,
            workerUser: Environment.GetEnvironmentVariable("COURSE_WORKER_USER") ?? PostgresRoles.Worker,
            workerPassword: Environment.GetEnvironmentVariable("COURSE_WORKER_PASSWORD") ?? string.Empty);
    }

    /// <summary>
    /// Читает параметры HTTP runtime. Publication-пароль API не использует.
    /// </summary>
    /// <returns>Параметры с заполненной runtime-ролью.</returns>
    /// <exception cref="InvalidOperationException">Нет host/db/runtime credentials.</exception>
    public static PostgresEndpoints FromRuntimeEnvironment()
    {
        return new PostgresEndpoints(
            host: ReadRequired("COURSE_DB_HOST"),
            port: ReadPort("COURSE_DB_PORT"),
            database: ReadRequired("COURSE_DB_NAME"),
            publicationUser: Environment.GetEnvironmentVariable("COURSE_PUBLICATION_USER") ?? PostgresRoles.Publication,
            publicationPassword: Environment.GetEnvironmentVariable("COURSE_PUBLICATION_PASSWORD") ?? string.Empty,
            runtimeUser: Environment.GetEnvironmentVariable("COURSE_RUNTIME_USER") ?? PostgresRoles.Runtime,
            runtimePassword: ReadRequired("COURSE_RUNTIME_PASSWORD"));
    }

    /// <summary>
    /// Читает параметры generic worker. Publication-пароль worker не использует.
    /// </summary>
    public static PostgresEndpoints FromWorkerEnvironment()
    {
        return new PostgresEndpoints(
            host: ReadRequired("COURSE_DB_HOST"),
            port: ReadPort("COURSE_DB_PORT"),
            database: ReadRequired("COURSE_DB_NAME"),
            publicationUser: Environment.GetEnvironmentVariable("COURSE_PUBLICATION_USER") ?? PostgresRoles.Publication,
            publicationPassword: Environment.GetEnvironmentVariable("COURSE_PUBLICATION_PASSWORD") ?? string.Empty,
            runtimeUser: Environment.GetEnvironmentVariable("COURSE_RUNTIME_USER") ?? PostgresRoles.Runtime,
            runtimePassword: Environment.GetEnvironmentVariable("COURSE_RUNTIME_PASSWORD") ?? string.Empty,
            workerUser: Environment.GetEnvironmentVariable("COURSE_WORKER_USER") ?? PostgresRoles.Worker,
            workerPassword: ReadRequired("COURSE_WORKER_PASSWORD"));
    }

    /// <summary>
    /// Возвращает описание без паролей и connection string.
    /// </summary>
    /// <returns>Хост, порт, база и имя роли publication.</returns>
    public override string ToString()
    {
        return $"Host={Host};Port={Port};Database={Database};User={PublicationUser}";
    }

    /// <summary>
    /// Читает обязательную строковую переменную окружения.
    /// </summary>
    /// <param name="name">Имя переменной.</param>
    /// <returns>Непустое значение.</returns>
    /// <exception cref="InvalidOperationException">Переменная отсутствует.</exception>
    private static string ReadRequired(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable {name} is required.");
        }

        return value;
    }

    /// <summary>
    /// Читает TCP-порт PostgreSQL.
    /// </summary>
    /// <param name="name">Имя переменной.</param>
    /// <returns>Номер порта.</returns>
    /// <exception cref="InvalidOperationException">Значение не является целым числом.</exception>
    private static int ReadPort(string name)
    {
        string raw = ReadRequired(name);
        if (!int.TryParse(raw, out int port) || port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"Environment variable {name} must be a TCP port.");
        }

        return port;
    }
}
