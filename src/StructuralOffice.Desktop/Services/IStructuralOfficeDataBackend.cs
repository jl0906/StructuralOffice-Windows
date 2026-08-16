using System.Text.Json.Nodes;
using StructuralOffice.Desktop.Models;

namespace StructuralOffice.Desktop.Services;

public interface IStructuralOfficeDataBackend : IStructuralOfficeBackend
{
    Task<BackendPage> GetLiveRecordsAsync(string collection, bool includeArchived = false,
        CancellationToken cancellationToken = default);
    Task<BackendRecord> CreateRecordAsync(string collection, JsonObject data,
        CancellationToken cancellationToken = default);
    Task<BackendRecord> UpdateRecordAsync(string collection, string id, int revision,
        JsonObject data, CancellationToken cancellationToken = default);
    Task<BackendRecord> ArchiveRecordAsync(string collection, string id, int revision,
        CancellationToken cancellationToken = default);
    Task<JsonObject> GetEditorsAsync(string collection, string id,
        CancellationToken cancellationToken = default);
    Task<JsonObject> StartEditingAsync(string collection, string id, string? sessionId = null,
        CancellationToken cancellationToken = default);
    Task EndEditingAsync(string collection, string id, string sessionId,
        CancellationToken cancellationToken = default);
    Task<BackendPage> GetTasksAsync(CancellationToken cancellationToken = default);
    Task<BackendPage> GetAccountingTasksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackendRecord>> GetAccountingTaskInvoicesAsync(string batchId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackendRecord>> GetAccountingRulesAsync(
        CancellationToken cancellationToken = default);
    Task<BackendRecord> UpdateAccountingRuleAsync(string id, int revision, JsonObject data,
        CancellationToken cancellationToken = default);
    Task<BackendPage> GetAuditAsync(CancellationToken cancellationToken = default);
    Task<BackendPage> GetEventsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackendRecord>> GetRolesAsync(CancellationToken cancellationToken = default);
    Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackendRecord>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackendRecord> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<BackendDownload> DownloadBackupAsync(string filename,
        CancellationToken cancellationToken = default);
    Task RestoreBackupAsync(string filename, CancellationToken cancellationToken = default);
    Task DeleteBackupAsync(string filename, CancellationToken cancellationToken = default);
    Task<JsonObject> ImportInvoiceCsvAsync(string filename, byte[] content, bool apply,
        CancellationToken cancellationToken = default);
    Task<JsonObject> PreviewInvoiceExcelAsync(byte[] content,
        CancellationToken cancellationToken = default);
    Task<JsonObject> ApplyInvoiceRecordsAsync(JsonArray records,
        CancellationToken cancellationToken = default);
    Task<BackendDownload> GenerateDocumentsAsync(JsonObject request,
        CancellationToken cancellationToken = default);
    Task SetOccurrenceStatusAsync(string id, string status,
        CancellationToken cancellationToken = default);
    Task<BackendDownload> ExportInvoicesAsync(bool emptyTemplate,
        CancellationToken cancellationToken = default);
    Task<BackendDownload> ExportInvoicesCsvAsync(CancellationToken cancellationToken = default);
    Task SendTestNotificationAsync(CancellationToken cancellationToken = default);
}
