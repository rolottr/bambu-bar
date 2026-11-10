# Loads the widget skins in the pinned Rainmeter on a Windows runner and screenshots the desktop (P2.8 evidence,
# P5.2 payload). Usage: pwsh rainmeter/ci/smoke.ps1 -Out <folder>
param([Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
New-Item -ItemType Directory -Force $Out | Out-Null

# 1. The pinned installer, checked, installed portably.
$pin = @{}
Get-Content (Join-Path $repo 'rainmeter/version.txt') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2] }
$installer = Join-Path $env:RUNNER_TEMP 'rainmeter-setup.exe'
Invoke-WebRequest $pin.url -OutFile $installer
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $pin.sha256) { throw "Rainmeter installer hash $hash isn't the pinned $($pin.sha256)" }
$portable = Join-Path $env:RUNNER_TEMP 'Rainmeter'
Start-Process $installer -ArgumentList '/S', '/PORTABLE=1', "/D=$portable" -Wait
if (-not (Test-Path (Join-Path $portable 'Rainmeter.exe'))) { throw 'Portable install failed' }
Compress-Archive -Path "$portable\*" -DestinationPath (Join-Path $Out 'rainmeter-portable.zip') -Force

# 2. The widgets folder as WidgetHost lays it out, with the sample states made current.
$root = Join-Path $env:RUNNER_TEMP 'Widgets'
$skins = Join-Path $root 'Skins'
$state = Join-Path $root 'State'
New-Item -ItemType Directory -Force $skins, $state | Out-Null
Copy-Item -Recurse (Join-Path $repo 'rainmeter/Skins/BambuBar') $skins
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
Get-ChildItem (Join-Path $repo 'rainmeter/samples') | ForEach-Object {
  if ($_.Extension -ne '.json') { Copy-Item $_.FullName $state; return }
  $json = Get-Content $_.FullName -Raw | ConvertFrom-Json
  $shift = $now - $json.savedAt
  foreach ($field in 'savedAt', 'updatedAt', 'endsAt') { if ($null -ne $json.$field) { $json.$field = $json.$field + $shift } }
  $json | ConvertTo-Json -Depth 20 -Compress | Set-Content -Encoding utf8NoBOM (Join-Path $state $_.Name)
}

# 3. One widget per scene, laid out in a grid (positions in Rainmeter.ini, as WidgetHost writes them).
$widgets = @(
  @('Small', 'printing'), @('Medium', 'printing'), @('Large', 'printing'), @('Small', 'paused'),
  @('Medium', 'a1-multicolour'), @('Large', 'h2d-two-nozzles'), @('Small', 'finished'), @('Medium', 'offline'),
  @('Large', 'unauthorized'), @('Small', 'connecting'), @('Medium', 'failed'), @('Small', 'unconfigured')
)
$ini = @('[Rainmeter]', "SkinPath=$skins\", 'TrayIcon=0', 'DisableVersionCheck=1', 'DisableAutoUpdate=1', 'Logging=1', 'Debug=1', '')
$x = 0; $y = 0; $rowHeight = 0
for ($i = 0; $i -lt $widgets.Count; $i++) {
  $size, $scene = $widgets[$i]
  $w = @{ Small = 212; Medium = 412; Large = 412 }[$size]; $h = @{ Small = 212; Medium = 212; Large = 412 }[$size]
  if ($x + $w -gt 1900) { $x = 0; $y += $rowHeight; $rowHeight = 0 }
  $config = "BambuBar\W$($i + 1)"
  $folder = Join-Path $skins $config
  New-Item -ItemType Directory -Force $folder | Out-Null
  "[Variables]`r`n@Include=#@#..\$size\$size.ini`r`nStateFile=$(Join-Path $state "$scene.json")`r`n" |
    Set-Content -Encoding utf8 (Join-Path $folder 'Widget.ini')
  $ini += "[$config]", 'Active=1', "WindowX=$x", "WindowY=$y", 'AlwaysOnTop=1', 'Draggable=0', ''
  $x += $w; $rowHeight = [Math]::Max($rowHeight, $h)
}
$iniPath = Join-Path $root 'Rainmeter.ini'
$ini -join "`r`n" | Set-Content -Encoding Unicode $iniPath

# 4. Run it, let the animations settle, screenshot twice (state, then a second later for the countdown/motion).
$process = Start-Process (Join-Path $portable 'Rainmeter.exe') -ArgumentList "`"$iniPath`"" -PassThru
Start-Sleep -Seconds 10
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
foreach ($shot in 1, 2) {
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $bitmap.Save((Join-Path $Out "widgets-$shot.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  Start-Sleep -Seconds 2
}
Copy-Item (Join-Path $root 'Rainmeter.log') $Out -ErrorAction SilentlyContinue
Stop-Process $process -ErrorAction SilentlyContinue
"Screen $($bounds.Width)x$($bounds.Height); Rainmeter $($pin.version) running: $(-not $process.HasExited)"
