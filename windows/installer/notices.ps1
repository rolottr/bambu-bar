# Writes THIRD-PARTY-NOTICES.txt into the install image (P5.1): the license of everything Bambu Bar ships that isn't
# its own, taken from the actual files. Run after the image is complete (publish + ffmpeg\ + Rainmeter\):
#   pwsh windows/installer/notices.ps1 -App build/windows/win-x64
# Sources: Rainmeter (GPL-2.0, rainmeter/version.txt for the version and source), FFmpeg (ffmpeg\LICENSE.txt and
# SOURCE.txt), the Nunito font (OFL.txt) and Fluent icons (Icons\LICENSE.txt) in the skins, and every NuGet package
# and .NET runtime pack that BambuBar.deps.json says the app was published with, licensed from the restored packages
# (the license file the package names, else a LICENSE file in it, else windows/installer/licenses/<id>.txt, else the
# MIT text with the package's copyright when its license expression is MIT). Fails when one has none of these.
param(
  [Parameter(Mandatory)] [string] $App,
  [string] $Out = (Join-Path $App 'THIRD-PARTY-NOTICES.txt'),
  [string] $Packages = $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget' 'packages' })
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$App = (Resolve-Path $App).Path
$rule = '=' * 100
$text = [System.Text.StringBuilder]::new()

function Add-Section([string] $title, [string] $about, [string[]] $files) {
  [void]$text.AppendLine($rule).AppendLine($title).AppendLine($rule)
  if ($about) { [void]$text.AppendLine($about.Trim()).AppendLine() }
  foreach ($file in $files) {
    if (-not (Test-Path $file)) { throw "Missing license file $file" }
    [void]$text.AppendLine((Get-Content $file -Raw).Trim()).AppendLine()
  }
  [void]$text.AppendLine()
}

function Read-Pin([string] $path) {
  $pin = @{}
  Get-Content $path | Where-Object { $_ -match '^([\w-]+)=(.+)$' } | ForEach-Object { $pin[$Matches[1]] = $Matches[2].Trim() }
  $pin
}

[void]$text.AppendLine('Bambu Bar for Windows: third-party notices').AppendLine()
[void]$text.AppendLine('Bambu Bar itself is MIT-licensed (LICENSE.txt). It ships the following software by others, each under its')
[void]$text.AppendLine('own license, reproduced below.').AppendLine().AppendLine()

# Rainmeter: GPL-2.0, unmodified (ledger D4). licenses/GPL-2.0.txt is the LICENSE file of its repository at the
# pinned tag.
$rainmeter = Read-Pin (Join-Path $repo 'rainmeter/version.txt')
$gpl = Join-Path $PSScriptRoot 'licenses/GPL-2.0.txt'
Add-Section "Rainmeter $($rainmeter.version) (Rainmeter\)" @"
Rainmeter draws the desktop widgets. It is shipped unmodified, as the Rainmeter project builds it
($($rainmeter.url)), and is licensed under the GNU General Public License version 2 (below).
Its complete source code for this version is attached to every Bambu Bar release on GitHub as
rainmeter-$($rainmeter.version)-source.zip (https://github.com/rolottr/bambu-bar/releases), and is also available from
$($rainmeter.source). We will send it to anyone who asks, for three years from the release.
Rainmeter includes LuaJIT, PCRE, zlib, Brotli, fmt, RapidJSON, kiss_fft and others; their licenses are in the
source archive under ThirdParty\.
Copyright (C) Rainmeter Team and contributors. https://github.com/rainmeter/rainmeter
"@ @($gpl)

# FFmpeg: LGPL 2.1 or later, with the source offer build.sh wrote.
$ffmpeg = Join-Path $App 'ffmpeg'
Add-Section 'FFmpeg (ffmpeg\)' 'The camera is decoded with FFmpeg libraries, dynamically linked. What was built and where its source is:' @(
  (Join-Path $ffmpeg 'SOURCE.txt'), (Join-Path $ffmpeg 'LICENSE.txt'))

$resources = Join-Path $App 'Skins/BambuBar/@Resources'
Add-Section 'Nunito font (Skins\BambuBar\@Resources\Fonts)' '' @(Join-Path $resources 'Fonts/OFL.txt')
Add-Section 'Fluent UI System Icons (Skins\BambuBar\@Resources\Icons)' '' @(Join-Path $resources 'Icons/LICENSE.txt')

# NuGet packages and runtime packs, as published.
$deps = Get-Content (Join-Path $App 'BambuBar.deps.json') -Raw | ConvertFrom-Json
$mit = @'
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
'@
$listed = @()
foreach ($library in $deps.libraries.PSObject.Properties) {
  if ($library.Value.type -notin 'package', 'runtimepack') { continue }
  $id, $version = $library.Name -replace '^runtimepack\.', '' -split '/'
  $folder = Join-Path $Packages $id.ToLowerInvariant() $version
  $nuspec = Get-ChildItem $folder -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $nuspec) { throw "No restored package for $id $version in $Packages" }
  $meta = ([xml](Get-Content $nuspec.FullName -Raw)).package.metadata
  $license = $meta.license
  $copyright = "$($meta.copyright)".Trim()
  $about = "Package $id $version$(if ($meta.projectUrl) { ", $($meta.projectUrl)" })$(if ($copyright) { "`n$copyright" })"
  $files = @()
  if ($license -and $license.type -eq 'file') { $files += Join-Path $folder $license.'#text' }
  elseif ($found = Get-ChildItem $folder -File | Where-Object Name -Match '^licen[cs]e(\.(md|txt))?$' | Select-Object -First 1) { $files += $found.FullName }
  elseif ($override = @("$id.txt", "$(($id -split '\.')[0]).txt") | ForEach-Object { Join-Path $PSScriptRoot 'licenses' $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1) { $files += $override }
  elseif ($license -and $license.'#text' -eq 'MIT') {
    $temp = New-TemporaryFile
    "MIT License`n`n$copyright`n`n$mit" | Set-Content $temp
    $files += $temp.FullName
  }
  elseif ($meta.licenseUrl -match 'WinSDKLicense') {
    # The C#/WinRT projection of the Windows SDK, which .NET apps that call Windows APIs carry; redistributable with them.
    $about += "`nLicensed under the Microsoft Windows SDK license terms: $($meta.licenseUrl)"
  }
  else { throw "No license found for $id $version (add windows/installer/licenses/$id.txt)" }
  # The package's own third-party notices travel with it.
  $files += Get-ChildItem $folder -File | Where-Object Name -Match '^third-?party-?notices\.txt$' | ForEach-Object FullName
  Add-Section $id $about $files
  $listed += "$id $version"
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$text.ToString() -replace "`r?`n", "`r`n" | Set-Content -Encoding utf8 -NoNewline $Out
"Wrote $Out ($([math]::Round((Get-Item $Out).Length / 1KB)) KB): Rainmeter, FFmpeg, Nunito, Fluent icons, $($listed.Count) packages"
$listed | ForEach-Object { "  $_" }
