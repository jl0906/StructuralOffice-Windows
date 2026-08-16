using System.IO;

namespace StructuralOffice.Desktop.Services;

public static class UpdateLog
{
    public static async Task WriteAsync(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StructuralOffice",
                "Logs");
            Directory.CreateDirectory(directory);
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(Path.Combine(directory, "updater.log"), line);
        }
        catch (IOException)
        {
            // Updating must never prevent the application from opening.
        }
        catch (UnauthorizedAccessException)
        {
            // Updating must never prevent the application from opening.
        }
    }
}
