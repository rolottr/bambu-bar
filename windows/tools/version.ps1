# Prints the app's version (X.Y.Z), shared by the Mac and Windows: MARKETING_VERSION in project.yml is the only place
# it's written. Directory.Build.props reads it the same way for the Windows build.
#   pwsh windows/tools/version.ps1
$ErrorActionPreference = 'Stop'
$spec = Join-Path $PSScriptRoot '..' '..' 'project.yml'
$match = Select-String -Path $spec -Pattern '^\s*MARKETING_VERSION:\s*"?(\d+\.\d+\.\d+)' | Select-Object -First 1
if (-not $match) { throw "No MARKETING_VERSION in $spec" }
$match.Matches[0].Groups[1].Value
