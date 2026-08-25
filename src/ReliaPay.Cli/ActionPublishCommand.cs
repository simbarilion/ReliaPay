using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Data;

namespace ReliaPay.Cli;

/// <summary>
/// Команда <c>action publish</c>: идемпотентная публикация версии в каталог.
/// </summary>
internal static class ActionPublishCommand
{
    /// <summary>
    /// Валидирует файл и вызывает <c>api.publish_action</c>.
    /// </summary>
    /// <param name="manifestPath">Путь к манифесту.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ноль при успехе.</returns>
    public static async Task<int> ExecuteAsync(string manifestPath, CancellationToken cancellationToken)
    {
        ActionManifestReadResult read = await ActionValidateCommand.ReadManifestFileAsync(manifestPath).ConfigureAwait(false);
        if (!read.IsSuccess || read.RawJson is null || read.Manifest is null)
        {
            return await CliFailure.WriteAsync(
                    read.ErrorCode ?? ErrorCodes.ManifestInvalid,
                    read.ErrorMessage ?? "manifest is invalid")
                .ConfigureAwait(false);
        }

        try
        {
            ActionCatalogClient client = CreateClient();
            CatalogMutationResult result = await client.PublishAsync(read.RawJson, cancellationToken).ConfigureAwait(false);
            return await WriteMutationAsync(result).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Печатает успех каталога или его ошибку.
    /// </summary>
    internal static async Task<int> WriteMutationAsync(CatalogMutationResult result)
    {
        if (!result.IsSuccess)
        {
            return await CliFailure.WriteAsync(
                    result.Code ?? ErrorCodes.InternalError,
                    result.Message ?? "catalog command failed")
                .ConfigureAwait(false);
        }

        await CliStdout.WriteAsync(
                new CliSuccessEnvelope(
                    "ok",
                    new CliSuccessResult(result.Resource, result.Operation, result.Key, result.Version),
                    new CliMeta(CourseContracts.Version)))
            .ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Собирает клиент каталога из переменных окружения CLI.
    /// </summary>
    internal static ActionCatalogClient CreateClient()
    {
        return new ActionCatalogClient(new PostgresConnectionFactory(PostgresEndpoints.FromEnvironment()));
    }
}
