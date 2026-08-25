using ReliaPay.Catalog;
using ReliaPay.Data;

namespace ReliaPay.Cli;

/// <summary>
/// Команда <c>action disable</c>: выключает версию и при необходимости назначает replacement.
/// </summary>
internal static class ActionDisableCommand
{
    /// <summary>
    /// Вызывает <c>api.disable_action</c>.
    /// </summary>
    /// <param name="route">Ключ <c>module.action</c>.</param>
    /// <param name="version">Выключаемая версия.</param>
    /// <param name="replacementVersion">Новая default либо null.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ноль при успехе.</returns>
    public static async Task<int> ExecuteAsync(
        string route,
        int version,
        int? replacementVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            ActionCatalogClient client = ActionPublishCommand.CreateClient();
            CatalogMutationResult result = await client
                .DisableAsync(route, version, replacementVersion, cancellationToken)
                .ConfigureAwait(false);
            return await ActionPublishCommand.WriteMutationAsync(result).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }
}
