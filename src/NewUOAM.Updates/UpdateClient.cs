using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NewUOAM.Updates;

/// <summary>Fetches the signed feed and downloads + verifies a package. Stateless apart from
/// its HttpClient.</summary>
public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _feedUrl;

    /// <param name="feedUrl">null = <see cref="UpdateFeed.DefaultFeedUrl"/>. Any other URL is for
    /// testing only, and then the package may come from anywhere (the signature still applies).</param>
    public UpdateClient(string? feedUrl = null)
    {
        _feedUrl = feedUrl ?? UpdateFeed.DefaultFeedUrl;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NewUOAM-updater", "1"));
    }

    public bool IsDefaultFeed => _feedUrl == UpdateFeed.DefaultFeedUrl;

    /// <summary>The verified manifest of the newest release, or throws. 404 (no release yet, or
    /// the repo isn't public) comes back as an <see cref="UpdateException"/> too.</summary>
    public async Task<UpdateManifest> GetLatestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        string json;
        try
        {
            using var response = await _http.GetAsync(_feedUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"Server aktualizací odpověděl {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > UpdateFeed.MaxFeedBytes)
                throw new UpdateException("Soubor aktualizace je podezřele velký.");
            json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (json.Length > UpdateFeed.MaxFeedBytes) throw new UpdateException("Soubor aktualizace je podezřele velký.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException("Server aktualizací není dostupný.", ex);
        }

        var manifest = UpdateFeed.ParseAndVerify(json);
        if (IsDefaultFeed && !manifest.PackageUrl.StartsWith(UpdateFeed.PackageUrlPrefix, StringComparison.Ordinal))
            throw new UpdateException("Balíček aktualizace má neočekávanou adresu.");
        return manifest;
    }

    /// <summary>Downloads the package to <paramref name="zipPath"/> and checks its size and
    /// SHA-256 against the signed manifest. Deletes the file and throws if they don't match.</summary>
    public async Task DownloadAsync(UpdateManifest manifest, string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        try
        {
            using var response = await _http.GetAsync(manifest.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"Stažení selhalo ({(int)response.StatusCode}).");

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > manifest.PackageSize) throw new UpdateException("Stažený balíček je větší, než má být.");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report((double)total / manifest.PackageSize);
                }
                if (total != manifest.PackageSize) throw new UpdateException("Stažený balíček je neúplný.");
            }
            if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("Kontrolní součet staženého balíčku nesedí.");
        }
        catch (Exception ex)
        {
            try { File.Delete(zipPath); } catch { }
            if (ex is HttpRequestException) throw new UpdateException("Stažení selhalo - zkontroluj připojení.", ex);
            throw;
        }
    }

    public void Dispose() => _http.Dispose();
}
