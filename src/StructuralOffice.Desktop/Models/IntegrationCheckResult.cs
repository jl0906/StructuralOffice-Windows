namespace StructuralOffice.Desktop.Models;

public enum CheckState
{
    Success,
    Warning,
    Error
}

public sealed record CheckItem(string Name, CheckState State, string Detail);

public sealed record IntegrationCheckResult(
    IReadOnlyList<CheckItem> Checks,
    string? IntegrationVersion,
    DateTimeOffset CheckedAt);
