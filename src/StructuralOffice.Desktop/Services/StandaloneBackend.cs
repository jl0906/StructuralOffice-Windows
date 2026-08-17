using StructuralOffice.Desktop.Models;

namespace StructuralOffice.Desktop.Services;

public sealed class StandaloneBackend : IStructuralOfficeBackend
{
    public string DisplayName => "Standalone";

    public Task<IntegrationCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var result = new IntegrationCheckResult(
            [new CheckItem(
                "Standalone", CheckState.Warning,
                UiLocalization.Choose(
                    "The local standalone service is planned for a later release.",
                    "Der lokale Standalone-Dienst ist für einen späteren Release vorgesehen."))],
            null,
            DateTimeOffset.Now);
        return Task.FromResult(result);
    }
}
