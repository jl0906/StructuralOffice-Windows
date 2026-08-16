using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task GetLiveRecordsAsync_ParsesRevisionedEnvelope()
    {
        var handler = new StubHandler(request => Json(HttpStatusCode.OK, """
            {"items":[{"id":"c-1","collection":"contacts","revision":3,
            "data":{"name":"Bau GmbH","email":"info@example.test"},
            "created_at":"2026-01-01T10:00:00Z","updated_at":"2026-01-02T10:00:00Z",
            "archived_at":null}],"total":1,"limit":500,"offset":0}
            """));
        using var client = new HttpClient(handler);
        using var backend = new HomeAssistantBackend(
            new Uri("https://ha.example.test"), "secret", client);

        var page = await backend.GetLiveRecordsAsync("contacts");

        var record = Assert.Single(page.Items);
        Assert.Equal("c-1", record.Id);
        Assert.Equal(3, record.Revision);
        Assert.Equal("Bau GmbH", record.Data["name"]!.GetValue<string>());
        Assert.Equal("/api/structuraloffice/v1/live/contacts", handler.LastPath);
        Assert.Equal("limit=500", handler.LastQuery.TrimStart('?'));
    }

    [Fact]
    public async Task UpdateRecordAsync_SendsRevisionAndData()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK,
                "{\"id\":\"c-1\",\"collection\":\"contacts\",\"revision\":5," +
                "\"data\":{\"name\":\"Neu\"}}");
        });
        using var client = new HttpClient(handler);
        using var backend = new HomeAssistantBackend(
            new Uri("https://ha.example.test"), "secret", client);

        var result = await backend.UpdateRecordAsync(
            "contacts", "c-1", 4, new JsonObject { ["name"] = "Neu" });

        Assert.Equal(HttpMethod.Patch, handler.LastMethod);
        Assert.Equal("/api/structuraloffice/v1/live/contacts/c-1", handler.LastPath);
        var payload = JsonNode.Parse(body!)!.AsObject();
        Assert.Equal(4, payload["expected_revision"]!.GetValue<int>());
        Assert.Equal("Neu", payload["data"]!["name"]!.GetValue<string>());
        Assert.Equal(5, result.Revision);
    }

    [Fact]
    public async Task UpdateRecordAsync_ExposesRevisionConflict()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Conflict, """
            {"code":"revision_conflict","error":"changed elsewhere","current":
            {"id":"c-1","collection":"contacts","revision":7,"data":{"name":"Aktuell"}}}
            """));
        using var client = new HttpClient(handler);
        using var backend = new HomeAssistantBackend(
            new Uri("https://ha.example.test"), "secret", client);

        var exception = await Assert.ThrowsAsync<BackendApiException>(() =>
            backend.UpdateRecordAsync("contacts", "c-1", 2,
                new JsonObject { ["name"] = "Alt" }));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("revision_conflict", exception.ErrorCode);
        Assert.Equal(7, exception.CurrentRecord!.Revision);
    }

    [Fact]
    public async Task ImportAndDocumentEndpoints_UseBackendContract()
    {
        var requests = new List<(string Path, string Body)>();
        var handler = new StubHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((request.RequestUri!.AbsolutePath, body));
            return request.RequestUri.AbsolutePath.EndsWith("/documents")
                ? Json(HttpStatusCode.OK,
                    "{\"filename\":\"Mahnung.pdf\",\"content\":\"SGFsbG8=\"}")
                : Json(HttpStatusCode.OK, "{\"created\":1,\"updated\":0}");
        });
        using var client = new HttpClient(handler);
        using var backend = new HomeAssistantBackend(
            new Uri("https://ha.example.test"), "secret", client);

        await backend.ImportInvoiceCsvAsync("liste.csv", Encoding.UTF8.GetBytes("a;b"), true);
        var download = await backend.GenerateDocumentsAsync(new JsonObject
        {
            ["document_type"] = "dunning_1",
            ["invoice_numbers"] = new JsonArray("R-1")
        });

        Assert.Equal("/api/structuraloffice/v1/imports/invoice-list", requests[0].Path);
        Assert.Contains("YTti", requests[0].Body);
        Assert.Equal("Mahnung.pdf", download.Filename);
        Assert.Equal("Hallo", Encoding.UTF8.GetString(download.Content));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public string? LastPath { get; private set; }
        public string LastQuery { get; private set; } = string.Empty;
        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastPath = request.RequestUri?.AbsolutePath;
            LastQuery = request.RequestUri?.Query ?? string.Empty;
            LastMethod = request.Method;
            return Task.FromResult(response(request));
        }
    }
}
