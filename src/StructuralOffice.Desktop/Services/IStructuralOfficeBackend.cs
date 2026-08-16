using StructuralOffice.Desktop.Models;

namespace StructuralOffice.Desktop.Services;

public interface IStructuralOfficeBackend
{
    string DisplayName { get; }

    Task<IntegrationCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}
