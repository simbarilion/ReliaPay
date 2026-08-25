using System.Net;

namespace ReliaPay.UnitTests.Gateway;

internal sealed class RecordingHandler : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }

    public string Method { get; private set; } = string.Empty;

    public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Body { get; private set; } = string.Empty;

    public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);

    public Exception? Throw { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        RequestUri = request.RequestUri;
        Method = request.Method.Method;
        Headers.Clear();
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            Headers[header.Key] = header.Value.ToArray();
        }

        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                Headers[header.Key] = header.Value.ToArray();
            }

            Body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        return Response;
    }
}
