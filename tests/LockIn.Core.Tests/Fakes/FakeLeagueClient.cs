using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LockIn.Core.Lcu;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Tests.Fakes;

public sealed record RecordedRequest(string Method, string Path, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>
/// A stand-in for the League client's local API: serves canned GET responses, records
/// everything else, and pushes WAMP events over a WebSocket — over plain HTTP on loopback.
/// </summary>
public sealed class FakeLeagueClient : IAsyncDisposable
{
    public const string Password = "test-password";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, string> _getResponses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<WebSocket> _sockets = [];
    private readonly string _directory;
    private readonly bool _ownsDirectory;

    private FakeLeagueClient(WebApplication app, string? installDirectory)
    {
        _app = app;
        _ownsDirectory = installDirectory is null;
        _directory = installDirectory ?? Directory.CreateTempSubdirectory("lockin-fakeclient-").FullName;
        Directory.CreateDirectory(_directory);
    }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public ConcurrentBag<string> Subscriptions { get; } = [];

    /// <summary>Optional: return a message to answer a non-GET request with a 500 error (it is still recorded).</summary>
    public Func<RecordedRequest, string?>? Reject { get; set; }

    /// <summary>Optional hook for non-GET requests, e.g. to update state and push an event.</summary>
    public Func<RecordedRequest, Task>? OnRequest { get; set; }

    /// <summary>Folder containing a lockfile pointing at this fake client.</summary>
    public string InstallDirectory => _directory;

    public static async Task<FakeLeagueClient> StartAsync(string? installDirectory = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeLeagueClient(app, installDirectory);
        app.UseWebSockets();
        app.Run(fake.HandleAsync);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address).Port;
        await File.WriteAllTextAsync(Path.Combine(fake._directory, "lockfile"), $"LeagueClient:{Environment.ProcessId}:{port}:{Password}:http");

        fake.SetGet("/lol-summoner/v1/current-summoner", new { summonerId = 1, gameName = "Tester", tagLine = "NA1" });
        return fake;
    }

    public void SetGet(string path, object? value) =>
        _getResponses[path] = JsonSerializer.Serialize(value, LcuClient.JsonOptions);

    public void RemoveGet(string path) => _getResponses.TryRemove(path, out _);

    public async Task PushAsync(string uri, object? data, string eventType = "Update")
    {
        var frame = JsonSerializer.Serialize(
            new object[] { 8, LcuEndpoints.EventName(uri), new { data, eventType, uri } },
            LcuClient.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(frame);
        foreach (var socket in _sockets.Where(s => s.State == WebSocketState.Open))
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
    }

    public IEnumerable<RecordedRequest> RequestsTo(string method, string pathPrefix) =>
        Requests.Where(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));

    private async Task HandleAsync(HttpContext context)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{Password}"));
        if (context.Request.Headers.Authorization != expected)
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await RunSocketAsync(await context.WebSockets.AcceptWebSocketAsync());
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (HttpMethods.IsGet(context.Request.Method))
        {
            if (_getResponses.TryGetValue(path, out var json))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(json);
            }
            else
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("""{"message":"not found"}""");
            }

            return;
        }

        using var reader = new StreamReader(context.Request.Body);
        var request = new RecordedRequest(context.Request.Method, path, await reader.ReadToEndAsync());
        Requests.Enqueue(request);
        if (Reject?.Invoke(request) is { } rejection)
        {
            // Like the real client turning a request down: an error status with a message.
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { errorCode = "RPC_ERROR", httpStatus = 500, message = rejection }));
            return;
        }

        if (OnRequest is { } hook)
        {
            await hook(request);
        }

        context.Response.StatusCode = 204;
    }

    private async Task RunSocketAsync(WebSocket socket)
    {
        _sockets.Add(socket);
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            // Subscription frames look like [5,"OnJsonApiEvent_…"].
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            if (doc.RootElement[0].GetInt32() == 5)
            {
                Subscriptions.Add(doc.RootElement[1].GetString()!);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (_ownsDirectory)
        {
            Directory.Delete(_directory, recursive: true);
        }
        else
        {
            File.Delete(Path.Combine(_directory, "lockfile"));
        }
    }
}
