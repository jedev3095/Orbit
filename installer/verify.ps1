[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$Installer,
 [Parameter(Mandatory)][string]$Payload,
 [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$Payload = (Resolve-Path -LiteralPath $Payload).Path
$WorkDirectory = (Resolve-Path -LiteralPath $WorkDirectory).Path
$target = [IO.Path]::GetFullPath((Join-Path $WorkDirectory 'OrbitInstallTest'))
if (!$target.StartsWith($WorkDirectory + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Test target escaped its work directory.' }
$uninstallKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/Orbit'
$installerKey = 'HKCU:/Software/Orbit/Installer'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Orbit'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Orbit.lnk'
if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $installerKey) -or (Test-Path -LiteralPath $menu) -or (Test-Path -LiteralPath $desktop) -or (Test-Path -LiteralPath $target)) {
 throw 'Existing installer state, shortcuts or test folder found. Use a clean Windows test account; nothing was changed.'
}
function DataHashes {
 $data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Orbit'
 $hashes = @{}
 if (Test-Path -LiteralPath $data) {
  Get-ChildItem -LiteralPath $data -File | ForEach-Object { $hashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash }
 }
 return $hashes
}
$existingApp = $null
if ([Threading.Mutex]::TryOpenExisting(('Orbit.AccountManager.' + [Environment]::UserName), [ref]$existingApp)) {
 $existingApp.Dispose(); throw 'Close Orbit before running installer verification. Nothing was changed.'
}
$expectedVersion = ([Version](Get-Item -LiteralPath (Join-Path $Payload 'Orbit.exe')).VersionInfo.FileVersion).ToString(3)
$before = DataHashes
# Prove that setup refuses a running app without launching the real app.
$mutex = [Threading.Mutex]::new($false, 'Orbit.AccountManager.' + [Environment]::UserName)
try {
 $blocked = Start-Process -FilePath $Installer -ArgumentList "/S /D=$target" -WindowStyle Hidden -PassThru
 $blocked.WaitForExit()
 if ($blocked.ExitCode -ne 2 -or (Test-Path -LiteralPath $target)) { throw 'Running-app guard failed.' }
} finally { $mutex.Dispose() }
$setup = Start-Process -FilePath $Installer -ArgumentList "/S /D=$target" -WindowStyle Hidden -PassThru
$setup.WaitForExit()
if ($setup.ExitCode -ne 0) { throw "Installer exited with $($setup.ExitCode)." }
$files = @(Get-ChildItem -LiteralPath $Payload -File -Recurse)
foreach ($file in $files) {
 $installed = Join-Path $target ([IO.Path]::GetRelativePath($Payload,$file.FullName))
 if (!(Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Installed payload differs from its published source.' }
}
$registry = Get-ItemProperty -LiteralPath $uninstallKey
if ($registry.InstallLocation -ne $target -or $registry.DisplayVersion -ne $expectedVersion) { throw 'Install registration mismatch.' }
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $menu 'Orbit.lnk'))
if ($link.TargetPath -ne (Join-Path $target 'Orbit.exe')) { throw 'Start menu shortcut target mismatch.' }
if (Test-Path -LiteralPath $desktop) { throw 'Optional desktop shortcut was unexpectedly created.' }
$canary = Join-Path $target 'keep-me.txt'
[IO.File]::WriteAllText($canary,'Synthetic user file: uninstall must preserve this.')
$canaryHash = (Get-FileHash -LiteralPath $canary).Hash
# Reinstall over the same location without deleting unknown files.
$upgrade = Start-Process -FilePath $Installer -ArgumentList "/S /D=$target" -WindowStyle Hidden -PassThru
$upgrade.WaitForExit()
if ($upgrade.ExitCode -ne 0 -or (Get-FileHash -LiteralPath $canary).Hash -ne $canaryHash) { throw "Reinstall failed (exit $($upgrade.ExitCode)) or changed a user file." }
if ((Get-ItemProperty -LiteralPath $uninstallKey).InstallLocation -ne $target) { throw 'Install target changed before uninstall; stopped.' }
$remove = Start-Process -FilePath (Join-Path $target 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru
$remove.WaitForExit()
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while (((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath (Join-Path $target 'Uninstall.exe'))) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $installerKey) -or (Test-Path -LiteralPath $menu)) { throw 'Uninstall left registration or start menu files.' }
$remaining = @(Get-ChildItem -LiteralPath $target -File -Recurse)
if ($remaining.Count -ne 1 -or $remaining[0].FullName -ne $canary -or (Get-FileHash -LiteralPath $canary).Hash -ne $canaryHash) { throw 'Uninstall removed a user file or left known program files.' }
$after = DataHashes
if ($before.Count -ne $after.Count) { throw 'Account data files changed during installer verification.' }
foreach ($name in $before.Keys) { if ($after[$name] -ne $before[$name]) { throw 'Account data hash changed during installer verification.' } }
# Delete only our synthetic file and the now-empty checked test directory.
Remove-Item -LiteralPath $canary
Remove-Item -LiteralPath $target
[ordered]@{status='passed';installedFiles=$files.Count;runningAppGuard=$true;startMenuShortcut=$true;desktopDefaultOff=$true;reinstall=$true;uninstall=$true;unknownFilesPreserved=$true;accountDataUnchanged=$true;missingWebViewBranchLiveTested=$false} | ConvertTo-Json
