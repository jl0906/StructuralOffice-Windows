using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StructuralOffice.Desktop.Models;

namespace StructuralOffice.Desktop.Services;

public sealed class HomeAssistantBackend : IStructuralOfficeDataBackend, IDisposable
{
    private const string ApiPrefix = "api/structuraloffice/v1/";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _accessToken;

    public HomeAssistantBackend(Uri baseAddress, string accessToken, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        _accessToken = accessToken.Trim();
        BaseAddress = NormalizeBaseAddress(baseAddress);
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = BaseAddress;
        _httpClient.Timeout = TimeSpan.FromSeconds(45);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseAddress { get; }

    public string DisplayName => "Home Assistant";

    public async Task<IntegrationCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = new List<CheckItem>();
        string? version = null;

        try
        {
            using var apiResponse = await _httpClient.GetAsync("api/", cancellationToken);
            if (apiResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                checks.Add(new CheckItem(
                    "Home Assistant", CheckState.Success, "Server ist erreichbar."));
                checks.Add(new CheckItem(
                    "Zugriffstoken", CheckState.Error,
                    "Das Token wurde von Home Assistant abgelehnt."));
                return Result(checks, version);
            }

            if (!apiResponse.IsSuccessStatusCode)
            {
                checks.Add(new CheckItem(
                    "Home Assistant", CheckState.Error,
                    $"Unerwartete Serverantwort: HTTP {(int)apiResponse.StatusCode}."));
                return Result(checks, version);
            }

            checks.Add(new CheckItem(
                "Home Assistant", CheckState.Success, "Server ist erreichbar."));
            checks.Add(new CheckItem(
                "Zugriffstoken", CheckState.Success, "Authentifizierung erfolgreich."));

            using var integrationResponse = await _httpClient.GetAsync(
                "api/structuraloffice/v1/status", cancellationToken);
            switch (integrationResponse.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Warning,
                        "Integration ist nicht installiert oder Home Assistant wurde noch nicht neu gestartet."));
                    break;
                case HttpStatusCode.ServiceUnavailable:
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Warning,
                        "Integration ist installiert, aber noch nicht konfiguriert."));
                    break;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Error,
                        "The Home Assistant account does not have access to StructuralOffice."));
                    break;
                default:
                    if (!integrationResponse.IsSuccessStatusCode)
                    {
                        checks.Add(new CheckItem(
                            "StructuralOffice", CheckState.Error,
                            $"Check failed: HTTP {(int)integrationResponse.StatusCode}."));
                        break;
                    }

                    version = await ReadVersionAsync(integrationResponse, cancellationToken);
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Success,
                        version is null
                            ? "Integration ist installiert und bereit."
                            : $"Integration {version} ist installiert und bereit."));
                    break;
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new CheckItem(
                "Home Assistant", CheckState.Error,
                "The request timed out. Check the address and network."));
        }
        catch (HttpRequestException exception)
        {
            checks.Add(new CheckItem(
                "Home Assistant", CheckState.Error, FriendlyNetworkError(exception)));
        }

        return Result(checks, version);
    }

    public async Task<BackendPage> GetLiveRecordsAsync(
        string collection,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var suffix = includeArchived ? "?include_archived=true&limit=500" : "?limit=500";
        var root = await GetJsonAsync($"live/{Escape(collection)}{suffix}", cancellationToken);
        return ParsePage(root, "items", liveEnvelope: true);
    }

    public async Task<BackendRecord> CreateRecordAsync(
        string collection,
        JsonObject data,
        CancellationToken cancellationToken = default)
    {
        var root = await SendJsonAsync(
            HttpMethod.Post,
            $"live/{Escape(collection)}",
            new JsonObject { ["data"] = data.DeepClone() },
            cancellationToken);
        return ParseRecord(root, true);
    }

    public async Task<BackendRecord> UpdateRecordAsync(
        string collection,
        string id,
        int revision,
        JsonObject data,
        CancellationToken cancellationToken = default)
    {
        var root = await SendJsonAsync(
            HttpMethod.Patch,
            $"live/{Escape(collection)}/{Escape(id)}",
            new JsonObject
            {
                ["data"] = data.DeepClone(),
                ["expected_revision"] = revision
            },
            cancellationToken);
        return ParseRecord(root, true);
    }

    public async Task<BackendRecord> ArchiveRecordAsync(
        string collection,
        string id,
        int revision,
        CancellationToken cancellationToken = default)
    {
        var root = await SendJsonAsync(
            HttpMethod.Delete,
            $"live/{Escape(collection)}/{Escape(id)}?expected_revision={revision}",
            null,
            cancellationToken);
        return ParseRecord(root, true);
    }

    public Task<JsonObject> GetEditorsAsync(
        string collection,
        string id,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync($"editing/{Escape(collection)}/{Escape(id)}", cancellationToken);

    public Task<JsonObject> StartEditingAsync(
        string collection,
        string id,
        string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(
            HttpMethod.Post,
            $"editing/{Escape(collection)}/{Escape(id)}",
            new JsonObject
            {
                ["client_id"] = "StructuralOffice-Windows",
                ["ttl_seconds"] = 300,
                ["session_id"] = sessionId
            },
            cancellationToken);

    public async Task EndEditingAsync(
        string collection,
        string id,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _ = await SendJsonAsync(
            HttpMethod.Delete,
            $"editing/{Escape(collection)}/{Escape(id)}?session_id={Escape(sessionId)}",
            null,
            cancellationToken);

    public async Task<BackendPage> GetTasksAsync(
        string? status = null,
        string? sourceType = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { "limit=500" };
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Escape(status)}");
        if (!string.IsNullOrWhiteSpace(sourceType)) query.Add($"source_type={Escape(sourceType)}");
        return ParsePage(await GetJsonAsync($"tasks?{string.Join('&', query)}", cancellationToken), "items");
    }

    public async Task<BackendRecord> GetTaskAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        ParseRecord(await GetJsonAsync($"tasks/{Escape(id)}", cancellationToken));

    public async Task<BackendRecord> CreateTaskAsync(
        JsonObject data,
        CancellationToken cancellationToken = default) =>
        ParseRecord(await SendJsonAsync(HttpMethod.Post, "tasks", data, cancellationToken));

    public async Task<BackendRecord> UpdateTaskAsync(
        string id,
        int revision,
        JsonObject data,
        CancellationToken cancellationToken = default) =>
        ParseRecord(await SendJsonAsync(HttpMethod.Patch, $"tasks/{Escape(id)}", new JsonObject
        {
            ["expected_revision"] = revision,
            ["data"] = data.DeepClone()
        }, cancellationToken));

    public async Task<BackendRecord> UpdateTaskChecklistItemAsync(
        string taskId,
        string itemId,
        int revision,
        JsonObject data,
        CancellationToken cancellationToken = default) =>
        ParseRecord(await SendJsonAsync(
            HttpMethod.Patch,
            $"tasks/{Escape(taskId)}/checklist/{Escape(itemId)}",
            new JsonObject
            {
                ["expected_revision"] = revision,
                ["data"] = data.DeepClone()
            },
            cancellationToken));

    public async Task<BackendPage> GetAccountingTasksAsync(
        CancellationToken cancellationToken = default) =>
        ParsePage(await GetJsonAsync("accounting/tasks?limit=500", cancellationToken), "items");

    public async Task<IReadOnlyList<BackendRecord>> GetAccountingTaskInvoicesAsync(
        string batchId,
        CancellationToken cancellationToken = default) =>
        ParseItems(
            await GetJsonAsync($"accounting/tasks/{Escape(batchId)}/invoices", cancellationToken),
            "invoices");

    public async Task<IReadOnlyList<BackendRecord>> GetAccountingRulesAsync(
        CancellationToken cancellationToken = default) =>
        ParseItems(await GetJsonAsync("accounting/rules", cancellationToken), "rules");

    public async Task<BackendRecord> UpdateAccountingRuleAsync(
        string id,
        int revision,
        JsonObject data,
        CancellationToken cancellationToken = default)
    {
        var root = await SendJsonAsync(
            HttpMethod.Patch,
            $"accounting/rules/{Escape(id)}",
            new JsonObject
            {
                ["data"] = data.DeepClone(),
                ["expected_revision"] = revision
            },
            cancellationToken);
        return ParseRecord(root);
    }

    public async Task<BackendPage> GetAuditAsync(CancellationToken cancellationToken = default) =>
        ParsePage(await GetJsonAsync("audit?limit=500", cancellationToken), "items");

    public async Task<BackendPage> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync("events?after=0&limit=1000", cancellationToken);
        var items = ParseItems(root, "events");
        return new BackendPage(items, items.Count, items.Count, 0);
    }

    public async Task<IReadOnlyList<BackendRecord>> GetRolesAsync(
        CancellationToken cancellationToken = default) =>
        ParseItems(await GetJsonAsync("roles", cancellationToken), "users");

    public Task SetRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            HttpMethod.Put,
            "roles",
            new JsonObject { ["user_id"] = userId, ["role"] = role },
            cancellationToken);

    public async Task<IReadOnlyList<BackendRecord>> GetBackupsAsync(
        CancellationToken cancellationToken = default) =>
        ParseItems(await GetJsonAsync("backups", cancellationToken), "backups", "filename");

    public async Task<BackendRecord> CreateBackupAsync(
        CancellationToken cancellationToken = default) =>
        ParseRecord(await SendJsonAsync(HttpMethod.Post, "backups", new JsonObject(), cancellationToken));

    public async Task<BackendDownload> DownloadBackupAsync(
        string filename,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"{ApiPrefix}backups/{Escape(filename)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return new BackendDownload(
            filename,
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public Task RestoreBackupAsync(
        string filename,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            HttpMethod.Post,
            $"backups/{Escape(filename)}",
            new JsonObject(),
            cancellationToken);

    public Task DeleteBackupAsync(
        string filename,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            HttpMethod.Delete,
            $"backups/{Escape(filename)}",
            null,
            cancellationToken);

    public Task<JsonObject> ImportInvoiceCsvAsync(
        string filename,
        byte[] content,
        bool apply,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(
            HttpMethod.Post,
            "imports/invoice-list",
            new JsonObject
            {
                ["apply"] = apply,
                ["content"] = Convert.ToBase64String(content),
                ["filename"] = filename
            },
            cancellationToken);

    public Task<JsonObject> PreviewInvoiceExcelAsync(
        byte[] content,
        CancellationToken cancellationToken = default) =>
        SendWebSocketCommandAsync(
            "structuraloffice/preview_invoice_import",
            new JsonObject { ["content"] = Convert.ToBase64String(content) },
            cancellationToken);

    public Task<JsonObject> ApplyInvoiceRecordsAsync(
        JsonArray records,
        CancellationToken cancellationToken = default) =>
        SendWebSocketCommandAsync(
            "structuraloffice/apply_invoice_import",
            new JsonObject { ["records"] = records.DeepClone() },
            cancellationToken);

    public async Task<BackendDownload> GenerateDocumentsAsync(
        JsonObject request,
        CancellationToken cancellationToken = default) =>
        ParseDownload(await SendJsonAsync(HttpMethod.Post, "documents", request, cancellationToken));

    public async Task SetOccurrenceStatusAsync(
        string id,
        string status,
        CancellationToken cancellationToken = default)
    {
        await SendWebSocketCommandAsync(
            "structuraloffice/set_occurrence_status",
            new JsonObject { ["occurrence_id"] = id, ["status"] = status },
            cancellationToken);
    }

    public async Task<BackendDownload> ExportInvoicesAsync(
        bool emptyTemplate,
        CancellationToken cancellationToken = default) =>
        ParseDownload(await SendWebSocketCommandAsync(
            "structuraloffice/export_invoices",
            new JsonObject { ["empty"] = emptyTemplate },
            cancellationToken));

    public async Task<BackendDownload> ExportInvoicesCsvAsync(
        CancellationToken cancellationToken = default) =>
        ParseDownload(await SendWebSocketCommandAsync(
            "structuraloffice/export_invoices_csv", null, cancellationToken));

    public async Task SendTestNotificationAsync(
        CancellationToken cancellationToken = default)
    {
        await SendWebSocketCommandAsync(
            "structuraloffice/test_notification", null, cancellationToken);
    }

    public async Task SubscribeLiveAsync(
        Func<JsonObject, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        using var socket = new ClientWebSocket();
        var socketUri = new UriBuilder(BaseAddress)
        {
            Scheme = BaseAddress.Scheme == "https" ? "wss" : "ws",
            Path = "/api/websocket"
        }.Uri;
        await socket.ConnectAsync(socketUri, cancellationToken);
        _ = await ReceiveSocketJsonAsync(socket, cancellationToken);
        await SendSocketJsonAsync(socket,
            new JsonObject { ["type"] = "auth", ["access_token"] = _accessToken },
            cancellationToken);
        var authentication = await ReceiveSocketJsonAsync(socket, cancellationToken);
        if (authentication["type"]?.GetValue<string>() != "auth_ok")
        {
            throw new BackendApiException("Home Assistant WebSocket authentication failed.", 401);
        }
        await SendSocketJsonAsync(socket, new JsonObject
        {
            ["id"] = 1,
            ["type"] = "structuraloffice/subscribe_live"
        }, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveSocketJsonAsync(socket, cancellationToken);
            if (message["type"]?.GetValue<string>() == "result" &&
                message["id"]?.GetValue<int>() == 1 &&
                message["success"]?.GetValue<bool>() != true)
            {
                var error = message["error"] as JsonObject;
                throw new BackendApiException(
                    error?["message"]?.GetValue<string>() ?? "Live-Aktualisierung abgelehnt.",
                    400,
                    error?["code"]?.GetValue<string>());
            }
            if (message["type"]?.GetValue<string>() == "event" &&
                message["id"]?.GetValue<int>() == 1 &&
                message["event"] is JsonObject liveEvent)
            {
                await onEvent(liveEvent);
            }
        }
    }

    public static Uri NormalizeBaseAddress(Uri value)
    {
        if (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS addresses are supported.");
        }

        var builder = new UriBuilder(value) { Path = "/", Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<JsonObject> GetJsonAsync(
        string relativePath,
        CancellationToken cancellationToken) =>
        await SendJsonAsync(HttpMethod.Get, relativePath, null, cancellationToken);

    private async Task SendWithoutResultAsync(
        HttpMethod method,
        string relativePath,
        JsonObject? payload,
        CancellationToken cancellationToken) =>
        _ = await SendJsonAsync(method, relativePath, payload, cancellationToken);

    private async Task<JsonObject> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        JsonObject? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, ApiPrefix + relativePath);
        if (payload is not null)
        {
            request.Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(root, (int)response.StatusCode);
        }
        return root;
    }

    private async Task<JsonObject> SendWebSocketCommandAsync(
        string commandType,
        JsonObject? values,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        var socketUri = new UriBuilder(BaseAddress)
        {
            Scheme = BaseAddress.Scheme == "https" ? "wss" : "ws",
            Path = "/api/websocket"
        }.Uri;
        await socket.ConnectAsync(socketUri, cancellationToken);
        _ = await ReceiveSocketJsonAsync(socket, cancellationToken);
        await SendSocketJsonAsync(
            socket,
            new JsonObject { ["type"] = "auth", ["access_token"] = _accessToken },
            cancellationToken);
        var authentication = await ReceiveSocketJsonAsync(socket, cancellationToken);
        if (authentication["type"]?.GetValue<string>() != "auth_ok")
        {
            throw new BackendApiException("Home Assistant WebSocket authentication failed.", 401);
        }

        var command = values?.DeepClone().AsObject() ?? new JsonObject();
        command["id"] = 1;
        command["type"] = commandType;
        await SendSocketJsonAsync(socket, command, cancellationToken);
        while (true)
        {
            var response = await ReceiveSocketJsonAsync(socket, cancellationToken);
            if (response["id"]?.GetValue<int>() != 1 ||
                response["type"]?.GetValue<string>() != "result")
            {
                continue;
            }
            if (response["success"]?.GetValue<bool>() == true)
            {
                return response["result"] as JsonObject ?? new JsonObject();
            }
            var error = response["error"] as JsonObject;
            throw new BackendApiException(
                error?["message"]?.GetValue<string>() ?? "Backend action failed.",
                400,
                error?["code"]?.GetValue<string>());
        }
    }

    private static async Task SendSocketJsonAsync(
        ClientWebSocket socket,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonObject> ReceiveSocketJsonAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new BackendApiException("The Home Assistant connection was closed.", 503);
            }
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonNode.Parse(stream.ToArray())?.AsObject()
               ?? throw new BackendApiException("Invalid WebSocket response.", 502);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        throw CreateApiException(root, (int)response.StatusCode);
    }

    private static BackendApiException CreateApiException(JsonObject root, int statusCode)
    {
        BackendRecord? current = null;
        if (root["current"] is JsonObject currentObject)
        {
            current = ParseRecord(currentObject, currentObject["data"] is JsonObject);
        }
        return new BackendApiException(
            root["error"]?.GetValue<string>() ?? $"Backend error HTTP {statusCode}.",
            statusCode,
            root["code"]?.GetValue<string>(),
            current);
    }

    private static BackendPage ParsePage(JsonObject root, string property, bool liveEnvelope = false)
    {
        var items = ParseItems(root, property, liveEnvelope: liveEnvelope);
        return new BackendPage(
            items,
            root["total"]?.GetValue<int>() ?? items.Count,
            root["limit"]?.GetValue<int>() ?? items.Count,
            root["offset"]?.GetValue<int>() ?? 0);
    }

    private static IReadOnlyList<BackendRecord> ParseItems(
        JsonObject root,
        string property,
        string idProperty = "id",
        bool liveEnvelope = false)
    {
        if (root[property] is not JsonArray array)
        {
            return [];
        }
        return array
            .OfType<JsonObject>()
            .Select(item => ParseRecord(item, liveEnvelope, idProperty))
            .ToList();
    }

    private static BackendRecord ParseRecord(
        JsonObject root,
        bool liveEnvelope = false,
        string idProperty = "id")
    {
        if (liveEnvelope || root["data"] is JsonObject)
        {
            return new BackendRecord(
                root["id"]?.GetValue<string>() ?? string.Empty,
                root["revision"]?.GetValue<int>() ?? 0,
                root["data"]?.DeepClone().AsObject() ?? new JsonObject(),
                root["collection"]?.GetValue<string>(),
                root["created_at"]?.GetValue<string>(),
                root["updated_at"]?.GetValue<string>(),
                root["archived_at"]?.GetValue<string>());
        }
        var clone = root.DeepClone().AsObject();
        return new BackendRecord(
            ValueAsString(root[idProperty]),
            root["revision"]?.GetValue<int>() ?? 0,
            clone,
            UpdatedAt: root["updated_at"]?.GetValue<string>());
    }

    private static BackendDownload ParseDownload(JsonObject root)
    {
        var filename = root["filename"]?.GetValue<string>() ?? "StructuralOffice-download";
        var encoded = root["content"]?.GetValue<string>()
                      ?? throw new BackendApiException("Download content is missing.", 502);
        try
        {
            return new BackendDownload(filename, Convert.FromBase64String(encoded));
        }
        catch (FormatException exception)
        {
            throw new BackendApiException("Download content is invalid.", 502, innerException: exception);
        }
    }

    private static string ValueAsString(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString()
    };

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static async Task<string?> ReadVersionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("version", out var version)
            ? version.GetString()
            : null;
    }

    private static IntegrationCheckResult Result(List<CheckItem> checks, string? version) =>
        new(checks, version, DateTimeOffset.Now);

    private static string FriendlyNetworkError(HttpRequestException exception)
    {
        if (exception.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return "TLS certificate validation failed. Check the certificate and address.";
        }

        return "The server is unreachable. Check the address, network, and port.";
    }
}
