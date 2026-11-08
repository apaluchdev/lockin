namespace LockIn.Core.Tests.Fakes;

/// <summary>An HTTP handler that fails every request, so tests never reach the internet.</summary>
public sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("no network in tests");
}
