using ReliaPay.Catalog;
using ReliaPay.Data;

namespace ReliaPay.Cli;

/// <summary>
/// Команда <c>action activate</c>: атомарно включает версию и делает её default.
/// </summary>
internal static class ActionActivateCommand
{
    /// <summary>
    /// Вызывает <c>api.activate_action</c>.
    /// </summary>
    /// <param name="route">Ключ <c>module.action</c>.</param>
    /// <param name="version">Версия.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ноль при успехе.</returns>
    public static async Task<int> ExecuteAsync(string route, int version, CancellationToken cancellationToken)
    {
        try
        {
            ActionCatalogClient client = ActionPublishCommand.CreateClient();
            CatalogMutationResult result = await client.ActivateAsync(route, version, cancellationToken).ConfigureAwait(false);
            return await ActionPublishCommand.WriteMutationAsync(result).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }
}
