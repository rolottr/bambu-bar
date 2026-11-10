# The Rainmeter that Bambu Bar ships in Rainmeter\ (ledger D4, P5.2), on a Windows machine: downloads the installer
# pinned in rainmeter/version.txt, checks its SHA-256, installs it portably into a work folder, and copies only what
# our skins need to -Out. Rainmeter is unmodified: files are left out, none is changed.
#   pwsh rainmeter/ci/payload.ps1 -Out build/windows/win-x64/Rainmeter [-SourceDir build/release]
# Kept: Rainmeter.exe, the top-level DLLs (Rainmeter.dll: LuaJIT and every measure our skins use, Script and
# ActionTimer, are built in, so no Plugins\), and Languages\1033.rmlang (English; Rainmeter falls back to 1033 when the
# configured language is missing). Left out: SkinInstaller.exe, Plugins\, the other languages, Defaults\Skins and
# Defaults\Layouts (illustro; Rainmeter only copies them when they exist), and Rainmeter.ini/Skins\ of the portable
# install. Rainmeter is x64 only: the ARM64 app ships this same payload, run under emulation (D9).
# -SourceDir also saves the matching source there as rainmeter-<ver>-source.zip (GitHub's archive of the tag,
# version.txt's source=), which every release carries (GPL-2.0).
param(
  [Parameter(Mandatory)] [string] $Out,
  [string] $SourceDir,
  [string] $Work = (Join-Path ([System.IO.Path]::GetTempPath()) "rainmeter-payload-$PID")
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$pin = @{}
Get-Content (Join-Path $repo 'rainmeter/version.txt') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2].Trim() }

# 1. The pinned installer, checked, installed portably.
New-Item -ItemType Directory -Force $Work | Out-Null
$installer = Join-Path $Work 'rainmeter-setup.exe'
Invoke-WebRequest $pin.url -OutFile $installer
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $pin.sha256) { throw "Rainmeter installer hash $hash isn't the pinned $($pin.sha256)" }
$portable = Join-Path $Work 'portable'
$setup = Start-Process $installer -ArgumentList '/S', '/PORTABLE=1', "/D=$portable" -Wait -PassThru
if ($setup.ExitCode -ne 0 -or -not (Test-Path (Join-Path $portable 'Rainmeter.exe'))) { throw "Portable install failed ($($setup.ExitCode))" }

# 2. Keep what the skins need.
$keep = @('Rainmeter.exe', 'Languages\1033.rmlang') + (Get-ChildItem $portable -File -Filter '*.dll' | ForEach-Object Name)
$keep += @(Get-ChildItem (Join-Path $portable 'Defaults') -Recurse -File -ErrorAction SilentlyContinue |
  ForEach-Object { [System.IO.Path]::GetRelativePath($portable, $_.FullName) } |
  Where-Object { $_ -notmatch '^Defaults\\(Skins|Layouts)\\' })
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
foreach ($file in $keep) {
  $source = Join-Path $portable $file
  if (-not (Test-Path $source)) { throw "The portable Rainmeter has no $file" }
  $target = Join-Path $Out $file
  New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
  Copy-Item $source $target
}
foreach ($required in 'Rainmeter.exe', 'Rainmeter.dll', 'Languages\1033.rmlang') {
  if (-not (Test-Path (Join-Path $Out $required))) { throw "Payload lacks $required" }
}

# 3. What was left out, and the sizes.
$all = Get-ChildItem $portable -Recurse -File
$kept = Get-ChildItem $Out -Recurse -File
"Rainmeter $($pin.version) portable: $($all.Count) files, $([math]::Round(($all | Measure-Object Length -Sum).Sum / 1MB, 2)) MB"
"Payload ($Out): $($kept.Count) files, $([math]::Round(($kept | Measure-Object Length -Sum).Sum / 1MB, 2)) MB"
$kept | ForEach-Object { "  kept     {0,10:N0}  {1}" -f $_.Length, [System.IO.Path]::GetRelativePath($Out, $_.FullName) }
$all | Where-Object { [System.IO.Path]::GetRelativePath($portable, $_.FullName) -notin $keep } |
  Group-Object { ([System.IO.Path]::GetRelativePath($portable, $_.FullName) -split '\\')[0] } |
  ForEach-Object { "  dropped  {0,10:N0}  {1} ({2} files)" -f ($_.Group | Measure-Object Length -Sum).Sum, $_.Name, $_.Count }

# 4. The source, for the release (GPL-2.0).
if ($SourceDir) {
  New-Item -ItemType Directory -Force $SourceDir | Out-Null
  $SourceZip = Join-Path $SourceDir "rainmeter-$($pin.version)-source.zip"
  Invoke-WebRequest $pin.source -OutFile $SourceZip
  "Source: $SourceZip ($([math]::Round((Get-Item $SourceZip).Length / 1MB, 2)) MB) from $($pin.source)"
}
Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
