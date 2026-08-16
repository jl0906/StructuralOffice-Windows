using System.Net;
using System.Net.Http;
using System.Text;
using StructuralOffice.Desktop.Services;
using Xunit;

namespace StructuralOffice.Desktop.Tests;

public sealed class HomeAssistantAuthServiceTests
{
    [Fact]
    public async Task RefreshAsync_ExchangesSavedRefreshToken()
    {
        string? requestBody = null;
        var handler = new AsyncStubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {
                  "access_token": "new-access-token",
                  "expires_in": 1800,
                  "token_type": "Bearer"
                }
                """);
        });
        using var client = new HttpClient(handler);
        using var auth = new HomeAssistantAuthService(client);

        var session = await auth.RefreshAsync(
            new Uri("http://homeassistant.local:8123"),
            "http://127.0.0.1:43123/",
            "saved-refresh-token");

        Assert.Equal("new-access-token", session.AccessToken);
        Assert.Equal("saved-refresh-token", session.RefreshToken);
        Assert.Contains("grant_type=refresh_token", requestBody);
        Assert.Contains("refresh_token=saved-refresh-token", requestBody);
        Assert.Contains("client_id=http%3A%2F%2F127.0.0.1%3A43123%2F", requestBody);
        Assert.Equal("/auth/token", handler.LastRequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RefreshAsync_RejectsInvalidTokenResponse()
    {
        var handler = new AsyncStubHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK, "{\"expires_in\":1800}")));
        using var client = new HttpClient(handler);
        using var auth = new HomeAssistantAuthService(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => auth.RefreshAsync(
            new Uri("https://home.example"),
            "http://127.0.0.1:43123/",
            "saved-refresh-token"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class AsyncStubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return await response(request);
        }
    }
}
