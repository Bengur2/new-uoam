using System.IO.Compression;
using System.Security.Cryptography;
using NewUOAM.Updates;

// Publisher-side release tool (see docs/RELEASE.md):
//   keygen --key <file>          new update-signing key (refuses to overwrite); prints the public key
//   pack   --dir <publish\NewUOAM> --version <x.y.z> --notes-file <file> --key <file> --out <dir>
//          [--package-base-url <url>]   writes NewUOAM.files, the zip and the signed update.json
//   verify --feed <update.json> [--zip <file>]   checks a feed (and package) like the app would

var opts = ParseArgs(args.Skip(1).ToArray());
try
{
    switch (args.FirstOrDefault())
    {
        case "keygen": return KeyGen(Required(opts, "key"));
        case "pack": return Pack(opts);
        case "verify": return Verify(opts);
        default:
            Console.Error.WriteLine("Usage: keygen | pack | verify (see Program.cs)");
            return 2;
    }
}
catch (Exception ex) when (ex is UpdateException or IOException or CryptographicException or ArgumentException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static int KeyGen(string keyPath)
{
    if (File.Exists(keyPath)) throw new IOException($"{keyPath} already exists - not overwriting a signing key.");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine($"Private key written to {keyPath} (keep it out of git, back it up).");
    Console.WriteLine("Public key for UpdateFeed.PublicKey:");
    Console.WriteLine(UpdateFeed.PublicKeyOf(key));
    return 0;
}

static int Pack(Dictionary<string, string> opts)
{
    string dir = Path.GetFullPath(Required(opts, "dir"));
    string versionText = Required(opts, "version");
    string notes = File.ReadAllText(Required(opts, "notes-file")).Trim();
    string outDir = Path.GetFullPath(Required(opts, "out"));
    var version = UpdateFeed.TryParseVersion(versionText) ?? throw new ArgumentException($"Bad version '{versionText}'.");
    if (Path.GetFileName(dir) != "NewUOAM") throw new ArgumentException("--dir must be a folder named NewUOAM (it becomes the zip's top folder).");
    if (!File.Exists(Path.Combine(dir, PackageInstaller.AppExeName))) throw new ArgumentException($"{PackageInstaller.AppExeName} not found in --dir.");

    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(Required(opts, "key")));
    string publicKey = UpdateFeed.PublicKeyOf(key);
    // A feed signed by another key would be rejected by every installed app.
    if (publicKey != UpdateFeed.PublicKey && !opts.ContainsKey("allow-foreign-key"))
        throw new CryptographicException("The key doesn't match UpdateFeed.PublicKey built into the app.");

    PackageInstaller.WriteFileList(dir);
    Directory.CreateDirectory(outDir);
    string zipName = $"NewUOAM-{version}-win-x64.zip";
    string zipPath = Path.Combine(outDir, zipName);
    File.Delete(zipPath);
    ZipFile.CreateFromDirectory(dir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);

    string baseUrl = opts.TryGetValue("package-base-url", out var b) ? b.TrimEnd('/') + "/" : $"{UpdateFeed.PackageUrlPrefix}v{version}/";
    var manifest = new UpdateManifest(
        UpdateFeed.Product, version.ToString(), DateTimeOffset.UtcNow, notes,
        baseUrl + zipName, UpdateFeed.Sha256OfFile(zipPath), new FileInfo(zipPath).Length);
    string feedPath = Path.Combine(outDir, "update.json");
    File.WriteAllText(feedPath, UpdateFeed.CreateSigned(manifest, key));

    // Round-trip exactly as the app will.
    UpdateFeed.ParseAndVerify(File.ReadAllText(feedPath), publicKey);
    Console.WriteLine($"Package: {zipPath} ({manifest.PackageSize / 1024 / 1024} MB, {PackageInstaller.ReadFileList(dir).Count} files)");
    Console.WriteLine($"Feed:    {feedPath}");
    Console.WriteLine($"URL:     {manifest.PackageUrl}");
    return 0;
}

static int Verify(Dictionary<string, string> opts)
{
    var manifest = UpdateFeed.ParseAndVerify(File.ReadAllText(Required(opts, "feed")));
    Console.WriteLine($"Signature OK: {manifest.Product} {manifest.Version}, {manifest.PublishedUtc:u}");
    Console.WriteLine($"Package: {manifest.PackageUrl}");
    if (opts.TryGetValue("zip", out var zip))
    {
        bool ok = UpdateFeed.Sha256OfFile(zip).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase)
                  && new FileInfo(zip).Length == manifest.PackageSize;
        Console.WriteLine(ok ? "Zip matches the manifest." : "ZIP DOES NOT MATCH THE MANIFEST.");
        if (!ok) return 1;
    }
    return 0;
}

static string Required(Dictionary<string, string> opts, string name) =>
    opts.TryGetValue(name, out var v) ? v : throw new ArgumentException($"Missing --{name}.");

static Dictionary<string, string> ParseArgs(string[] a)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        string name = a[i][2..];
        bool hasValue = i + 1 < a.Length && !a[i + 1].StartsWith("--");
        result[name] = hasValue ? a[++i] : "true";
    }
    return result;
}
