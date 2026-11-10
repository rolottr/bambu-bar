# Loads the widget skins in the pinned Rainmeter on a Windows runner and screenshots the desktop (P2.8 evidence,
# with the P5.2 payload). Usage: pwsh rainmeter/ci/smoke.ps1 -Out <folder>
param([Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
New-Item -ItemType Directory -Force $Out | Out-Null

# 1. The trimmed Rainmeter the app ships (payload.ps1: pinned installer, checked, installed portably, trimmed), so this
#    also proves the skins need nothing that was left out.
$pin = @{}
Get-Content (Join-Path $repo 'rainmeter/version.txt') | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2] }
$portable = Join-Path $env:RUNNER_TEMP 'Rainmeter'
& (Join-Path $PSScriptRoot 'payload.ps1') -Out $portable
Compress-Archive -Path "$portable\*" -DestinationPath (Join-Path $Out 'rainmeter-payload.zip') -Force

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

# 3. Batches that fit the runner's 1024 x 768 screen, one screenshot each (positions in Rainmeter.ini, as
#    WidgetHost writes them). Each batch runs its own Rainmeter, closed before the next.
$batches = @(
  @(@('Small', 'printing', 0, 0), @('Medium', 'printing', 212, 0), @('Small', 'paused', 0, 212), @('Medium', 'a1-multicolour', 212, 212), @('Small', 'preparing', 624, 0), @('Small', 'finished', 624, 212)),
  @(@('Large', 'printing', 0, 0), @('Large', 'h2d-two-nozzles', 412, 0)),
  @(@('Large', 'paused', 0, 0), @('Large', 'failed', 412, 0)),
  @(@('Large', 'finished', 0, 0), @('Large', 'idle', 412, 0)),
  @(@('Small', 'offline', 0, 0), @('Small', 'connecting', 212, 0), @('Small', 'unauthorized', 424, 0), @('Small', 'unconfigured', 636, 0),
    @('Medium', 'offline', 0, 212), @('Medium', 'failed', 412, 212), @('Medium', 'idle', 0, 424), @('Medium', 'unauthorized', 412, 424)),
  @(@('Large', 'unauthorized', 0, 0), @('Large', 'unconfigured', 412, 0))
)
$iniPath = Join-Path $root 'Rainmeter.ini'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$n = 0
for ($b = 0; $b -lt $batches.Count; $b++) {
  Get-ChildItem $skins\BambuBar -Directory -Filter 'W*' | Remove-Item -Recurse -Force
  $ini = @('[Rainmeter]', "SkinPath=$skins\", 'TrayIcon=0', 'DisableVersionCheck=1', 'DisableAutoUpdate=1', 'Logging=1', '')
  foreach ($widget in $batches[$b]) {
    $size, $scene, $x, $y = $widget
    $n++
    $config = "BambuBar\W$n"
    $folder = Join-Path $skins $config
    New-Item -ItemType Directory -Force $folder | Out-Null
    "[Variables]`r`n@Include=#@#..\$size\$size.ini`r`nStateFile=$(Join-Path $state "$scene.json")`r`n" |
      Set-Content -Encoding utf8 (Join-Path $folder 'Widget.ini')
    $ini += "[$config]", 'Active=1', "WindowX=$x", "WindowY=$y", 'AlwaysOnTop=1', 'Draggable=0', ''
  }
  $ini -join "`r`n" | Set-Content -Encoding Unicode $iniPath
  $process = Start-Process (Join-Path $portable 'Rainmeter.exe') -ArgumentList "`"$iniPath`"" -PassThru
  Start-Sleep -Seconds 8
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $bitmap.Save((Join-Path $Out "widgets-$($b + 1).png"), [System.Drawing.Imaging.ImageFormat]::Png)
  Get-Content (Join-Path $root 'Rainmeter.log') -ErrorAction SilentlyContinue | Add-Content (Join-Path $Out 'Rainmeter.log')
  Get-Process Rainmeter -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Seconds 1
}
$errors = Select-String -Path (Join-Path $Out 'Rainmeter.log') -Pattern '^(ERRO|WARN)' -ErrorAction SilentlyContinue
"Screen $($bounds.Width)x$($bounds.Height); Rainmeter $($pin.version); $($batches.Count) screenshots; log errors/warnings: $(@($errors).Count)"
if (@($errors).Count -gt 0) { $errors | ForEach-Object { $_.Line }; throw 'Rainmeter logged errors or warnings' }
