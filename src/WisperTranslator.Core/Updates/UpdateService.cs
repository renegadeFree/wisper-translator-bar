using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WisperTranslator.Core.Updates;

/// <summary>Una release leggibile di GitHub con l'installer da scaricare.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    string? ChecksumUrl,
    long Size);

/// <summary>Installer scaricato e verificato, pronto da avviare.</summary>
public sealed record UpdateDownload(string Path, string Sha256, bool Verified);

/// <summary>
/// Aggiornamenti dalle release GitHub: controlla l'ultima versione, scarica l'installer,
/// ne verifica lo SHA-256 e lo avvia in modalità silenziosa. La repository è pubblica, quindi
/// non serve un token; l'API senza token ha però un limite orario di richieste.
/// </summary>
public static class UpdateService
{
    public const string Repository = "renegadeFree/wisper-translator-bar";
    public const string SetupPrefix = "WisperTranslator-Setup";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Versione dell'applicazione in esecuzione (dall'assembly di avvio).</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Versione leggibile: la stessa forma dei tag ("2.52").</summary>
    public static string Format(Version version) => $"{version.Major}.{version.Minor}";

    public static string CurrentText => Format(CurrentVersion);

    /// <summary>"v2.52" / "2.52" → Version; le etichette con suffissi non sono confrontabili.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0);
        var cleaned = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(cleaned, out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    public static bool IsNewer(Version candidate, Version current) => candidate > current;

    /// <summary>Legge la risposta di <c>releases/latest</c> e sceglie l'installer.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement)
            || !TryParseTag(tagElement.GetString() ?? string.Empty, out var version)
            || !root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? name = null;
        string? url = null;
        string? checksum = null;
        long size = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var assetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (assetUrl is null)
            {
                continue;
            }

            if (name is null
                && assetName.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase)
                && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = assetName;
                url = assetUrl;
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            else if (assetName.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase))
            {
                checksum = assetUrl;
            }
        }

        return name is null || url is null
            ? null
            : new ReleaseInfo(version, tagElement.GetString() ?? string.Empty, name, url, checksum, size);
    }

    /// <summary>Ultima release pubblicata (le prerelease non vengono considerate).</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Add("User-Agent", "WisperTranslator");
        request.Headers.Add("Accept", "application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return ParseRelease(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Scarica l'installer nella cartella temporanea e verifica lo SHA-256 pubblicato dalla
    /// release. Se il file di firma manca, il download resta valido ma non verificato.
    /// </summary>
    public static async Task<UpdateDownload> DownloadAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), release.AssetName);
        var temporary = path + ".part";
        using (var response = await Http.GetAsync(
                   release.DownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(temporary);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    progress?.Report(Math.Clamp((double)done / total, 0, 1));
                }
            }
        }

        File.Move(temporary, path, overwrite: true);
        var actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
        var expected = release.ChecksumUrl is null
            ? null
            : await TryDownloadChecksumAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
        if (expected is not null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("Il file scaricato non corrisponde alla firma della release.");
        }

        return new UpdateDownload(path, actual, expected is not null);
    }

    /// <summary>Avvia l'installer in silenzio; al termine riapre l'applicazione.</summary>
    public static Process StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            // /UPDATE dice all'installer di riavviare l'app al termine (vedi installer.iss).
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE",
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Non è stato possibile avviare l'installer.");

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string?> TryDownloadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var text = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var trimmed = text.Trim();
            // Formato: "<sha256>  <nome file>"
            var hash = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return hash is { Length: 64 } ? hash : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
