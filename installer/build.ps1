[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NsisPath,
    [Parameter(Mandatory)][string]$WebViewInstaller,
    [string]$RuntimeVersion = '8.0.33',
    [string]$OutputDirectory = '',
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$compiler = (Resolve-Path -LiteralPath $NsisPath).Path
$webView = (Resolve-Path -LiteralPath $WebViewInstaller).Path
$signature = Get-AuthenticodeSignature -LiteralPath $webView
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation(?:,|$)') {
    throw 'The WebView2 standalone installer must have a valid Microsoft Corporation signature.'
}
# Reject the tiny online bootstrapper: this installer promises offline setup.
if ((Get-Item -LiteralPath $webView).Length -lt 50MB) { throw 'Use the full x64 WebView2 Evergreen Standalone Installer, not the bootstrapper.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'Orbit.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part application version.' }
$buildRoot = Join-Path $repoRoot ("publish/installer-$version-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
$payload = Join-Path $buildRoot 'app'
$generated = Join-Path $buildRoot 'generated'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'publish/installers' }
New-Item -ItemType Directory -Path $generated,$OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
# A unique publish directory keeps stale files and personal data out of the package.
if (Test-Path -LiteralPath $payload) { throw 'The fresh build directory unexpectedly exists.' }
& dotnet publish (Join-Path $repoRoot 'Orbit.csproj') -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$repoRoot=/_/Orbit" -o $payload --nologo
if ($LASTEXITCODE) { throw 'Application publish failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $payload
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
$forbidden = @($files | Where-Object { $_.Name -match '^(vault\.dat|logins\.dat|activity\.json|backup-policy\.)|\.(csv|orbit|orbitlogins|pdb)$' -or $_.FullName -match '[\\/](Browsers|WebView|TestResults)[\\/]' })
if ($forbidden.Count) { throw 'Private data, debug symbols or test output found in publish payload.' }
$config = Get-Content -LiteralPath (Join-Path $payload 'Orbit.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!$config.runtimeOptions.includedFrameworks -or $config.runtimeOptions.frameworks) { throw 'The installer payload must include its .NET runtime.' }
$uninstall = Join-Path $generated 'uninstall-files.nsh'
$lines = [Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($payload,$file.FullName)
    if ($relative.Contains('..') -or $relative.Contains('$') -or $relative.Contains('"')) { throw 'Unsafe payload filename.' }
    $lines.Add('Delete "$INSTDIR\' + $relative + '"')
}
Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
    $lines.Add('RMDir "$INSTDIR\' + [IO.Path]::GetRelativePath($payload,$_.FullName) + '"')
}
$lines | Set-Content -LiteralPath $uninstall -Encoding utf8
$installedKb = [int][Math]::Ceiling(($files | Measure-Object Length -Sum).Sum / 1KB)
$output = Join-Path $OutputDirectory "Orbit-Setup-$version-x64.exe"
& $compiler /V3 "/DAPP_VERSION=$version" "/DPAYLOAD_DIR=$payload" "/DWEBVIEW_INSTALLER=$webView" "/DUNINSTALL_INCLUDE=$uninstall" "/DOUTPUT_FILE=$output" "/DINSTALLED_KB=$installedKb" (Join-Path $PSScriptRoot 'Orbit.nsi')
if ($LASTEXITCODE) { throw 'NSIS compilation failed.' }
[ordered]@{
    version = $version
    architecture = 'win-x64'
    compilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    dotnetRuntime = $RuntimeVersion
    offlineWebViewInstallerSha256 = (Get-FileHash -LiteralPath $webView -Algorithm SHA256).Hash
    installerSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    installerBytes = (Get-Item -LiteralPath $output).Length
    payloadFiles = $files.Count
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory "Orbit-Setup-$version-x64.build.json") -Encoding utf8
$installerHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
($installerHash + '  ' + [IO.Path]::GetFileName($output)) | Set-Content -LiteralPath ($output + '.sha256') -Encoding ascii
if ($Verify) {
    $verification = Join-Path $buildRoot 'verification'
    New-Item -ItemType Directory -Path $verification -Force | Out-Null
    & (Join-Path $PSScriptRoot 'verify.ps1') -Installer $output -Payload $payload -WorkDirectory $verification
}
Write-Output $output
