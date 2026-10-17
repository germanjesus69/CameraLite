# Builds the self-contained portable package and zip in dist\.
# The viewer and its transport worker each carry the .NET runtime, so the
# package runs on Windows x64 without installing .NET. vendor-sdk\ (the TUTK
# DLLs, not in the repository) is included when present beside this script.
param([string]$Version = '0.4.1-preview')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$name = "CameraLite-$Version-win-x64"
$out = Join-Path $root "dist\$name"
$zip = Join-Path $root "dist\$name.zip"
foreach ($old in @($out, $zip)) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force } }

dotnet publish (Join-Path $root 'src\CameraLite\CameraLite.csproj') -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'Viewer publish failed.' }
# The build copies a framework-dependent worker; replace it with a self-contained one.
$transport = Join-Path $out 'transport'
if (Test-Path -LiteralPath $transport) { Remove-Item -LiteralPath $transport -Recurse -Force }
dotnet publish (Join-Path $root 'src\CameraLite.Transport.Host\CameraLite.Transport.Host.csproj') -c Release -r win-x64 --self-contained true -o $transport
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
# Publishing the worker self-contained rewrote its normal build output; restore it for development builds.
dotnet build (Join-Path $root 'src\CameraLite.Transport.Host\CameraLite.Transport.Host.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Worker rebuild failed.' }

# The worker runs from transport\; the viewer's project reference also drops copies in the root.
Get-ChildItem -LiteralPath $out -File -Filter 'CameraLite.Transport.Host.*' | Remove-Item -Force
Get-ChildItem -LiteralPath $out -Recurse -File -Filter '*.pdb' | Remove-Item -Force
foreach ($arch in @('win-x86', 'win-arm64')) {
    $unused = Join-Path $out "libvlc\$arch"
    if (Test-Path -LiteralPath $unused) { Remove-Item -LiteralPath $unused -Recurse -Force }
}
if (-not (Test-Path -LiteralPath (Join-Path $out 'vendor-sdk\IOTCAPIs.dll'))) { Write-Warning 'vendor-sdk\ is missing: UID cameras need a custom SDK folder in this package.' }
foreach ($doc in @('README.md', 'RELEASE_NOTES.md', 'THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $root $doc) -Destination $out }
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination (Join-Path $out 'licenses') -Recurse

# MIT license and notices of the bundled .NET runtime, from the runtime packs used above.
$frameworks = (Get-Content -LiteralPath (Join-Path $out 'CameraLite.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
foreach ($framework in $frameworks) {
    $pack = "$($framework.name).Runtime.win-x64"
    $folders = @((Join-Path $env:USERPROFILE ".nuget\packages\$($pack.ToLowerInvariant())\$($framework.version)"), (Join-Path $env:ProgramFiles "dotnet\packs\$pack\$($framework.version)"))
    # Packs name the file LICENSE.TXT or LICENSE; not every pack has separate notices.
    $license = $folders | ForEach-Object { Join-Path $_ 'LICENSE.TXT'; Join-Path $_ 'LICENSE' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $license) { throw "The license of $pack $($framework.version) was not found." }
    $short = if ($framework.name -like '*WindowsDesktop*') { 'windowsdesktop' } else { 'dotnet' }
    Copy-Item -LiteralPath $license -Destination (Join-Path $out "licenses\$short-runtime-LICENSE.txt")
    $notices = Join-Path (Split-Path -Parent $license) 'THIRD-PARTY-NOTICES.TXT'
    if (Test-Path -LiteralPath $notices) { Copy-Item -LiteralPath $notices -Destination (Join-Path $out "licenses\$short-runtime-THIRD-PARTY-NOTICES.txt") }
}
$runtime = $frameworks[0].version

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$item = Get-Item -LiteralPath $zip
[pscustomobject]@{ zip = $item.FullName; MiB = [Math]::Round($item.Length / 1MB, 1); sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash; runtime = $runtime } | Format-List
