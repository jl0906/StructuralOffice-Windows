using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using StructuralOffice.Desktop.Services;
using Xunit;

namespace StructuralOffice.Desktop.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckAndInstallAsync_DownloadsVerifiedNewerRelease()
    {
        var installer = Encoding.UTF8.GetBytes("verified installer payload");
        var digest = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "api.github.com" => Json(HttpStatusCode.OK, ReleaseJson("v0.2.0-alpha", true, digest)),
            "github.com" => Bytes(HttpStatusCode.OK, installer),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var client = new HttpClient(handler);
        var directory = Path.Combine(Path.GetTempPath(), $"structuraloffice-update-{Guid.NewGuid():N}");
        string? launched = null;

        try
        {
            using var updater = new UpdateService(
                client, "0.1.0-alpha", directory, path => launched = path);

            Assert.True(await updater.CheckAndInstallAsync());
            Assert.Equal(Path.Combine(directory, UpdateService.InstallerAssetName), launched);
            Assert.Equal(installer, await File.ReadAllBytesAsync(launched!));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CheckAndInstallAsync_RejectsInvalidDigest()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "api.github.com" => Json(HttpStatusCode.OK, ReleaseJson(
                "v0.2.0-alpha", true, new string('0', 64))),
            "github.com" => Bytes(HttpStatusCode.OK, Encoding.UTF8.GetBytes("tampered")),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var client = new HttpClient(handler);
        var directory = Path.Combine(Path.GetTempPath(), $"structuraloffice-update-{Guid.NewGuid():N}");

        try
        {
            using var updater = new UpdateService(client, "0.1.0-alpha", directory, _ => { });
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.CheckAndInstallAsync());
            Assert.False(File.Exists(Path.Combine(directory, UpdateService.InstallerAssetName)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FindUpdateAsync_StableVersionIgnoresPrerelease()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.OK,
            ReleaseJson("v2.0.0-alpha", true, new string('a', 64))));
        using var client = new HttpClient(handler);
        using var updater = new UpdateService(client, "1.0.0", installerLauncher: _ => { });

        Assert.Null(await updater.FindUpdateAsync());
    }

    [Theory]
    [InlineData("0.1.0-alpha", "0.1.0", -1)]
    [InlineData("0.1.0", "0.2.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    public void ReleaseVersion_UsesSemanticOrdering(string left, string right, int expectedSign)
    {
        Assert.True(ReleaseVersion.TryParse(left, out var leftVersion));
        Assert.True(ReleaseVersion.TryParse(right, out var rightVersion));
        Assert.Equal(expectedSign, Math.Sign(leftVersion!.CompareTo(rightVersion)));
    }

    private static string ReleaseJson(string version, bool prerelease, string digest) => $$"""
        [{
          "tag_name": "{{version}}",
          "draft": false,
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [{
            "name": "StructuralOffice_Install.exe",
            "browser_download_url": "https://github.com/jl0906/StructuralOffice-Windows/releases/download/{{version}}/StructuralOffice_Install.exe",
            "digest": "sha256:{{digest}}"
          }]
        }]
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Bytes(HttpStatusCode status, byte[] content) => new(status)
    {
        Content = new ByteArrayContent(content)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
