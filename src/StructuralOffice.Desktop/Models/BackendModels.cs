using System.Text.Json.Nodes;

namespace StructuralOffice.Desktop.Models;

public sealed record BackendRecord(
    string Id,
    int Revision,
    JsonObject Data,
    string? Collection = null,
    string? CreatedAt = null,
    string? UpdatedAt = null,
    string? ArchivedAt = null);

public sealed record BackendPage(
    IReadOnlyList<BackendRecord> Items,
    int Total,
    int Limit,
    int Offset);

public sealed record BackendDownload(string Filename, byte[] Content);

public sealed class BackendApiException : Exception
{
    public BackendApiException(
        string message,
        int statusCode,
        string? errorCode = null,
        BackendRecord? currentRecord = null,
        Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        CurrentRecord = currentRecord;
    }

    public int StatusCode { get; }

    public string? ErrorCode { get; }

    public BackendRecord? CurrentRecord { get; }
}
