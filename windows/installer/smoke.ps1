# Installs Bambu Bar silently on a Windows runner, checks what landed, runs the installed app's --snapshot (FFmpeg,
# assets and the panels from the install location), uninstalls silently and checks that only the user's data is left.
#   pwsh windows/installer/smoke.ps1 -Installer build/installer/Bambu-Bar-<ver>-windows-x64.exe -Out build/install-smoke
# TODO (P5.1): the silent update path: install over a running copy (started with --background and one widget) and
# check it is stopped, replaced and started again with --background, with its Rainmeter.
param([Parameter(Mandatory)] [string] $Installer, [Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$app = Join-Path $env:LOCALAPPDATA 'Programs\Bambu Bar'
$classes = 'HKCU:\Software\Classes'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4EE34AFD-22B6-4447-84F3-ED113FD0A4F1}_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Bambu Bar.lnk'
$userData = Join-Path $env:APPDATA 'Bambu Bar'

# 1. Install.
$setup = Start-Process $Installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $Out 'install.log')" -Wait -PassThru
if ($setup.ExitCode -ne 0) { throw "Setup exited with $($setup.ExitCode)" }
$expected = 'BambuBar.exe', 'AppIcon.png', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'unins000.exe',
  'Assets\Localizable.xcstrings', 'ffmpeg\avcodec-63.dll', 'ffmpeg\LICENSE.txt', 'ffmpeg\SOURCE.txt',
  'Rainmeter\Rainmeter.exe', 'Rainmeter\Rainmeter.dll', 'Rainmeter\Languages\1033.rmlang',
  'Skins\version.txt', 'Skins\BambuBar\Small\Small.ini', 'Skins\BambuBar\@Resources\Scripts\widget.lua'
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $app $_)) }
if ($missing) { throw "Not installed: $($missing -join ', ')" }
foreach ($key in "$classes\bambubar\shell\open\command", "$classes\AppUserModelId\dev.rolottr.BambuBar", $uninstallKey) {
  if (-not (Test-Path $key)) { throw "Missing registry key $key" }
}
$command = (Get-ItemProperty "$classes\bambubar\shell\open\command").'(default)'
if ($command -ne "`"$app\BambuBar.exe`" `"%1`"") { throw "bambubar:// opens '$command'" }
if (-not (Test-Path $shortcut)) { throw "No Start menu shortcut at $shortcut" }
$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum
"Installed to $app ($([math]::Round($size / 1MB, 1)) MB); installer $([math]::Round((Get-Item $Installer).Length / 1MB, 1)) MB"

# 2. The installed app finds its FFmpeg and assets.
$snapshots = Join-Path $Out 'snapshots'
$p = Start-Process (Join-Path $app 'BambuBar.exe') -ArgumentList '--snapshot', $snapshots -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installed snapshot exited with $($p.ExitCode)" }
foreach ($errors in 'camera-errors.txt', 'panel-errors.txt', 'window-errors.txt') {
  $path = Join-Path $snapshots $errors
  if (Test-Path $path) { Get-Content $path; throw "The installed app's snapshot wrote $errors" }
}

# 3. What the app adds while it runs (sign-in entry, settings), as if it had: the uninstaller must remove the first and
#    keep the second.
Set-ItemProperty $run -Name 'Bambu Bar' -Value "`"$app\BambuBar.exe`" --background"
New-Item -ItemType Directory -Force $userData | Out-Null
'{}' | Set-Content (Join-Path $userData 'settings.json')

# 4. Uninstall (the uninstaller hands over to a copy in %TEMP% and returns: wait for the folder to go).
$uninstallLog = Join-Path $env:TEMP 'bambubar-uninstall.log'
$p = Start-Process (Join-Path $app 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$uninstallLog`"" -Wait -PassThru
$uninstallExit = $p.ExitCode
for ($i = 0; $i -lt 60 -and (Test-Path $app); $i++) { Start-Sleep -Seconds 1 }
# Inno's uninstaller hands over to a copy in %TEMP%: keep whatever logs it wrote, and say what it returned.
Copy-Item $uninstallLog (Join-Path $Out 'uninstall.log') -ErrorAction SilentlyContinue
Get-ChildItem $env:TEMP -Filter 'Setup Log*.txt' -ErrorAction SilentlyContinue | Copy-Item -Destination $Out
"Uninstaller exit code: $uninstallExit"
if (Test-Path $uninstallLog) { Get-Content $uninstallLog | Select-Object -Last 30 } else { 'The uninstaller wrote no log.' }
$left = @()
if (Test-Path $app) { $left += "$app ($(@(Get-ChildItem $app -Recurse).Count) entries)" }
foreach ($key in "$classes\bambubar", "$classes\AppUserModelId\dev.rolottr.BambuBar", $uninstallKey) { if (Test-Path $key) { $left += $key } }
if ((Get-ItemProperty $run -ErrorAction SilentlyContinue).'Bambu Bar') { $left += "$run\Bambu Bar" }
if (Test-Path $shortcut) { $left += $shortcut }
if ($left) { throw "Left after uninstall: $($left -join '; ')" }
if (-not (Test-Path (Join-Path $userData 'settings.json'))) { throw 'The uninstaller removed the settings' }
if ($uninstallExit -ne 0) { throw "The uninstaller cleaned up but exited with $uninstallExit" }
'Uninstalled: app folder, keys, shortcut and sign-in entry gone; settings kept'
