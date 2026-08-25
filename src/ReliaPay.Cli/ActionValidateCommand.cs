using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Json;

namespace ReliaPay.Cli;

/// <summary>
/// Команда <c>action validate</c>: schema-проверка без записи в PostgreSQL.
/// </summary>
internal static class ActionValidateCommand
{
    /// <summary>
    /// Читает файл и печатает один JSON в stdout.
    /// </summary>
    /// <param name="manifestPath">Путь внутри контейнера.</param>
    /// <returns>Ноль при успехе.</returns>
    public static async Task<int> ExecuteAsync(string manifestPath)
    {
        ActionManifestReadResult read = await ReadManifestFileAsync(manifestPath).ConfigureAwait(false);
        if (!read.IsSuccess || read.Manifest is null)
        {
            return await CliFailure.WriteAsync(
                    read.ErrorCode ?? ErrorCodes.ManifestInvalid,
                    read.ErrorMessage ?? "manifest is invalid")
                .ConfigureAwait(false);
        }

        await CliStdout.WriteAsync(
                new CliSuccessEnvelope(
                    "ok",
                    new CliSuccessResult("action", "validated", read.Manifest.RouteKey, read.Manifest.Version),
                    new CliMeta(CourseContracts.Version)))
            .ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Читает файл манифеста с диска.
    /// </summary>
    /// <param name="manifestPath">Путь к JSON.</param>
    /// <returns>Результат разбора.</returns>
    internal static async Task<ActionManifestReadResult> ReadManifestFileAsync(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return new ActionManifestReadResult(
                null,
                null,
                ErrorCodes.RequestInvalid,
                "manifest file does not exist");
        }

        string json = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ActionManifestReadResult(
                null,
                null,
                ErrorCodes.ManifestInvalid,
                "manifest is not valid JSON");
        }

        ActionManifestReader reader = new(new JsonDocumentValidator());
        return reader.Read(json);
    }
}
