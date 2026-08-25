using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Data;

namespace ReliaPay.Cli;

/// <summary>
/// Команда <c>action list</c>: снимок опубликованных версий.
/// </summary>
internal static class ActionListCommand
{
    /// <summary>
    /// Читает <c>api.actions</c> и печатает один JSON.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ноль при успехе.</returns>
    public static async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            ActionCatalogClient client = ActionPublishCommand.CreateClient();
            IReadOnlyList<ActionListEntry> entries = await client.ListAsync(cancellationToken).ConfigureAwait(false);
            CliListedAction[] items = [.. entries.Select(static entry => new CliListedAction(
                entry.Module,
                entry.Action,
                entry.Version,
                entry.Enabled,
                entry.IsDefault))];
            await CliStdout.WriteAsync(
                    new CliSuccessEnvelope(
                        "ok",
                        new CliSuccessResult("action", "listed", "catalog", Version: null, Items: items),
                        new CliMeta(CourseContracts.Version)))
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }
}
