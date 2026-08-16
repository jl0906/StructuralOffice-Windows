using System.Net;
using System.Net.Http;
using System.Text;
using StructuralOffice.Desktop.Models;
using StructuralOffice.Desktop.Services;
using Xunit;

namespace StructuralOffice.Desktop.Tests;

public sealed class HomeAssistantBackendTests
{
    [Fact]
    public async Task CheckAsync_ReportsReadyIntegrationAndVersion()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/" => Json(HttpStatusCode.OK, "{\"message\":\"API running.\"}"),
            "/api/structuraloffice/v1/status" => Json(HttpStatusCode.OK, "{\"version\":\"0.4.0-alpha\"}"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var client = new HttpClient(handler);
        using var backend = new HomeAssistantBackend(
            new Uri("http://homeassistant.local:8123"), "secret", client);

        var result = await backend.CheckAsync();

        Assert.Equal("0.4.0-alpha", result.IntegrationVersion);
        Assert.Equal(3, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.Equal(CheckState.Success, check.State));
    }

    [Fact]
    public async Task CheckAsync_StopsAfterRejectedToken()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        using var client = new HttpClient(handler);
        using var backend = new HomeAssistantBackend(
            new Uri("http://homeassistant.local:8123"), "wrong", client);

        var result = await backend.CheckAsync();

        Assert.Collection(
            result.Checks,
            check => Assert.Equal(CheckState.Success, check.State),
            check => Assert.Equal(CheckState.Error, check.State));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckAsync_ReportsMissingIntegration()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/api/"
            ? Json(HttpStatusCode.OK, "{}")
            : Json(HttpStatusCode.NotFound, "{}"));
        using var client = new HttpClient(handler);
        using var backend = new HomeAssistantBackend(
            new Uri("https://example.test/prefix/"), "secret", client);

        var result = await backend.CheckAsync();

        Assert.Equal(CheckState.Warning, result.Checks[^1].State);
        Assert.Contains("nicht installiert", result.Checks[^1].Detail);
    }

    [Theory]
    [InlineData("http://example.test", "http://example.test/")]
    [InlineData("https://example.test:8123/base", "https://example.test:8123/")]
    public void NormalizeBaseAddress_AddsTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, HomeAssistantBackend.NormalizeBaseAddress(new Uri(input)).ToString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response(request));
        }
    }
}
