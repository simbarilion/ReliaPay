using System.Text.Json;
using ReliaPay.Api.Health;

namespace ReliaPay.Api;

/// <summary>
/// Точка входа внутреннего action runtime. Host-порт наружу не публикуется; доступен только через gateway.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Запускает HTTP-процесс api с liveness и каркасом готовности.
    /// </summary>
    /// <param name="args">Аргументы хоста ASP.NET Core.</param>
    /// <returns>Код завершения процесса.</returns>
    public static async Task<int> Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        WebApplication app = builder.Build();
        app.MapGet("/health/live", () => Results.Json(new ApiHealthPayload("live")));
        app.MapGet("/health/ready", () => Results.Json(new ApiHealthPayload("ready")));
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
