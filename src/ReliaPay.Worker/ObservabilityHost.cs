using System.Net;
using System.Text;
using ReliaPay.Data;

namespace ReliaPay.Worker;

/// <summary>
/// Служебный HTTP на :8080 — live/ready/metrics рядом с claim-циклом.
/// </summary>
internal sealed class ObservabilityHost : IAsyncDisposable
{
    private static readonly byte[] LiveBody = Encoding.UTF8.GetBytes("""{"status":"live"}""");
    private static readonly byte[] ReadyBody = Encoding.UTF8.GetBytes("""{"status":"ready"}""");
    private static readonly byte[] NotReadyBody =
        Encoding.UTF8.GetBytes("""{"status":"not_ready","code":"dependency.unavailable"}""");
    private static readonly byte[] EmptyMetrics = Encoding.UTF8.GetBytes("# EOF\n");
    private const string MetricsType = "application/openmetrics-text; version=1.0.0; charset=utf-8";

    private readonly HttpListener _listener = new();
    private readonly PostgresConnectionFactory _connections;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ObservabilityHost(PostgresConnectionFactory connections, string prefix = "http://*:8080/")
    {
        _connections = connections;
        _listener.Prefixes.Add(prefix);
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            if (path == "/health/live")
            {
                await WriteAsync(context, 200, "application/json", LiveBody).ConfigureAwait(false);
                return;
            }

            if (path == "/health/ready")
            {
                bool ready = await ProbePostgresAsync().ConfigureAwait(false);
                if (ready)
                {
                    await WriteAsync(context, 200, "application/json", ReadyBody).ConfigureAwait(false);
                }
                else
                {
                    await WriteAsync(context, 503, "application/json", NotReadyBody).ConfigureAwait(false);
                }

                return;
            }

            if (path == "/metrics")
            {
                await WriteAsync(context, 200, MetricsType, EmptyMetrics).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<bool> ProbePostgresAsync()
    {
        try
        {
            await using var connection = await _connections.OpenWorkerConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteAsync(
        HttpListenerContext context,
        int status,
        string contentType,
        byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }

        _listener.Close();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _cts.Dispose();
    }
}
