# Builds the player package and (with -Publish) releases it on GitHub. See docs/RELEASE.md.
#
#   powershell -ExecutionPolicy Bypass -File tools\Release\release.ps1 -NotesFile notes.txt            # build only
#   powershell -ExecutionPolicy Bypass -File tools\Release\release.ps1 -NotesFile notes.txt -Publish   # build + GitHub release
#
# The version comes from <Version> in src\NewUOAM.App\NewUOAM.App.csproj. Output goes to
# publish\app\<version>\ (zip + update.json). ASCII only on purpose: PowerShell 5.1 reads a
# BOM-less script as ANSI.
param(
    [string]$NotesFile,
    [string]$Notes,
    [switch]$Publish,
    [string]$KeyPath,
    # Testing only: where the package will be downloaded from instead of the GitHub release.
    [string]$PackageBaseUrl
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $KeyPath) { $KeyPath = Join-Path $root 'update-signing.key' }

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

# --- version and notes -------------------------------------------------------------------------
$csproj = Get-Content (Join-Path $root 'src\NewUOAM.App\NewUOAM.App.csproj') -Raw
if ($csproj -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') { throw 'No <Version>x.y.z</Version> in NewUOAM.App.csproj' }
$version = $Matches[1]
$tag = "v$version"

if (-not (Test-Path $KeyPath)) { throw "Signing key not found: $KeyPath (see docs/RELEASE.md)" }
$notesPath = Join-Path $env:TEMP "newuoam-notes-$version.txt"
if ($NotesFile) { Copy-Item $NotesFile $notesPath -Force }
elseif ($Notes) { [IO.File]::WriteAllText($notesPath, $Notes, (New-Object Text.UTF8Encoding $false)) }
else { throw 'Give -NotesFile or -Notes (what is new, shown to players in the update dialog).' }

# --- a release must be exactly the pushed, public source ---------------------------------------
if ($Publish) {
    Push-Location $root
    try {
        if (git status --porcelain --untracked-files=no) { throw 'Uncommitted changes - commit and push first.' }
        Run git @('fetch', '--quiet', 'origin')
        $head = (git rev-parse HEAD).Trim()
        $remote = (git rev-parse origin/main).Trim()
        if ($head -ne $remote) { throw 'HEAD is not origin/main - push first (the release is built from the public source).' }
        # "release not found" goes to stderr, which PowerShell 5.1 turns into a terminating error
        # under ErrorActionPreference=Stop; run it through cmd so only the exit code counts.
        cmd /c "gh release view $tag >nul 2>nul"
        if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists - bump <Version> in NewUOAM.App.csproj." }
    } finally { Pop-Location }
}

# --- build -------------------------------------------------------------------------------------
$out = Join-Path $root "publish\app\$version"
$appDir = Join-Path $out 'NewUOAM'
$bridgeDir = Join-Path $out 'bridge'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "== Publishing NewUOAM.App $version (self-contained)"
Run dotnet @('publish', (Join-Path $root 'src\NewUOAM.App'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:DebugType=none', '-p:SatelliteResourceLanguages=cs', '-o', $appDir)

# Players have no .NET installed and the bridge runs from its own copy in %LocalAppData%, so it
# can't use the app's runtime: ship it as one self-contained exe instead of dll + apphost.
Write-Host '== Publishing NewUOAM.UoaBridge (single file)'
Run dotnet @('publish', (Join-Path $root 'src\NewUOAM.UoaBridge'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:PublishTrimmed=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-o', $bridgeDir)
foreach ($f in 'NewUOAM.UoaBridge.dll', 'NewUOAM.UoaBridge.deps.json', 'NewUOAM.UoaBridge.runtimeconfig.json') {
    Remove-Item (Join-Path $appDir $f) -ErrorAction SilentlyContinue
}
Copy-Item (Join-Path $bridgeDir 'NewUOAM.UoaBridge.exe') (Join-Path $appDir 'NewUOAM.UoaBridge.exe') -Force
Remove-Item $bridgeDir -Recurse -Force

Write-Host '== Packing and signing'
$packArgs = @('run', '--project', (Join-Path $root 'tools\NewUOAM.ReleaseTool'), '-c', 'Release', '--', 'pack',
    '--dir', $appDir, '--version', $version, '--notes-file', $notesPath, '--key', $KeyPath, '--out', $out)
if ($PackageBaseUrl) { $packArgs += @('--package-base-url', $PackageBaseUrl) }
Run dotnet $packArgs

$zip = Join-Path $out "NewUOAM-$version-win-x64.zip"
$feed = Join-Path $out 'update.json'

# --- release -----------------------------------------------------------------------------------
if ($Publish) {
    Write-Host "== Creating GitHub release $tag"
    Push-Location $root
    try {
        Run gh @('release', 'create', $tag, $zip, $feed, '--title', "new UOAM $version", '--notes-file', $notesPath, '--target', $head)
    } finally { Pop-Location }
    Write-Host "Released $tag. Installed maps will offer it at their next start."
} else {
    Write-Host "Built $out (not published; add -Publish to release it)."
}
