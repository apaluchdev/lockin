using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LockIn.Core.Lcu;

public sealed class LcuRequestException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// HTTP access to the League client's local API.
/// </summary>
public sealed class LcuClient : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public LcuClient(LcuLockfile lockfile)
    {
        Lockfile = lockfile;
        var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = LcuCertificate.Callback },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = lockfile.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Authorization = CreateAuthHeader(lockfile);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public LcuLockfile Lockfile { get; }

    internal static AuthenticationHeaderValue CreateAuthHeader(LcuLockfile lockfile) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{LcuLockfile.UserName}:{lockfile.Password}")));

    /// <summary>GETs a resource. Returns default when the client answers 404 (resource does not exist right now).</summary>
    public async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, path, null, hasBody: false, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        await EnsureSuccessAsync(response, HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public Task PostAsync(string path, object? body = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, path, body, body is not null, cancellationToken);

    public Task PatchAsync(string path, object body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, path, body, hasBody: true, cancellationToken);

    public Task PutAsync(string path, object body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, path, body, hasBody: true, cancellationToken);

    private async Task SendAsync(HttpMethod method, string path, object? body, bool hasBody, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, hasBody, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, method, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, bool hasBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (hasBody)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LcuRequestException($"{method} /{path.TrimStart('/')} failed: {ex.Message}", inner: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LcuRequestException($"{method} /{path.TrimStart('/')} timed out", inner: ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = TryGetErrorMessage(text) ?? text;
        if (message.Length > 300)
        {
            message = message[..300];
        }

        throw new LcuRequestException(
            $"{method} /{path.TrimStart('/')} returned {(int)response.StatusCode} {response.StatusCode}: {message}",
            response.StatusCode);
    }

    private static string? TryGetErrorMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
