using WisperTranslator.Core.Updates;

namespace WisperTranslator.Tests;

/// <summary>
/// Lettura delle release GitHub e confronto delle versioni: sono le due parti che decidono se
/// un aggiornamento esiste davvero, quindi si verificano senza rete.
/// </summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v2.52", 2, 52)]
    [InlineData("2.52", 2, 52)]
    [InlineData("V2.5.0", 2, 5)]
    public void IGateVengonoLetti(string tag, int major, int minor)
    {
        Assert.True(UpdateService.TryParseTag(tag, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
    }

    [Theory]
    [InlineData("v2.2.0-raycast.1")]
    [InlineData("ultima")]
    [InlineData("")]
    public void IGateNonConfrontabiliVengonoScartati(string tag)
    {
        Assert.False(UpdateService.TryParseTag(tag, out _));
    }

    [Fact]
    public void SoloUnaVersionePiuAltaEEUnAggiornamento()
    {
        Assert.True(UpdateService.IsNewer(new Version(2, 52), new Version(2, 51)));
        Assert.False(UpdateService.IsNewer(new Version(2, 51), new Version(2, 51)));
        Assert.False(UpdateService.IsNewer(new Version(2, 50), new Version(2, 51)));
    }

    [Fact]
    public void LaReleaseSceglieLInstallerELaFirma()
    {
        const string json = """
        {
          "tag_name": "v2.52",
          "assets": [
            { "name": "WisperTranslator-Windows-x64.zip", "browser_download_url": "https://example.com/x.zip", "size": 140490037 },
            { "name": "WisperTranslator-Setup-2.52.exe", "browser_download_url": "https://example.com/setup.exe", "size": 94163258 },
            { "name": "WisperTranslator-Setup-2.52.exe.sha256", "browser_download_url": "https://example.com/setup.exe.sha256", "size": 99 }
          ]
        }
        """;

        var release = UpdateService.ParseRelease(json);

        Assert.NotNull(release);
        Assert.Equal(new Version(2, 52), release!.Version);
        Assert.Equal("v2.52", release.Tag);
        Assert.Equal("WisperTranslator-Setup-2.52.exe", release.AssetName);
        Assert.Equal("https://example.com/setup.exe", release.DownloadUrl);
        Assert.Equal("https://example.com/setup.exe.sha256", release.ChecksumUrl);
        Assert.Equal(94163258, release.Size);
    }

    [Fact]
    public void UnaReleaseSenzaInstallerNonEUnAggiornamento()
    {
        const string json = """
        { "tag_name": "v2.52", "assets": [
            { "name": "WisperTranslator-Windows-x64.zip", "browser_download_url": "https://example.com/x.zip", "size": 10 }
        ] }
        """;

        Assert.Null(UpdateService.ParseRelease(json));
    }

    [Fact]
    public void LaVersioneMostrataUsaLaStessaFormaDeiTag()
    {
        Assert.Equal("2.52", UpdateService.Format(new Version(2, 52, 0, 0)));
    }
}
