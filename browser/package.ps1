# SPDX-License-Identifier: MIT
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('chromium', 'firefox')]
    [string]$Browser,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$sourcePath = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
if ($outputPath.TrimEnd('\') -eq $sourcePath.TrimEnd('\') -or $outputPath.StartsWith($sourcePath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an output directory outside the browser source directory.'
}
$packagePath = Join-Path $outputPath ('qing-browser-' + $Browser)
if (Test-Path -LiteralPath $packagePath) {
    throw "Output already exists: $packagePath"
}
$zipPath = Join-Path $outputPath ('qing-browser-' + $Browser + '.zip')
if (Test-Path -LiteralPath $zipPath) {
    throw "Archive already exists: $zipPath"
}
$licensePath = Join-Path $PSScriptRoot '..\LICENSE'
if (-not (Test-Path -LiteralPath $licensePath)) {
    throw "License file is missing: $licensePath"
}
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null
$files = @('background.js', 'content.js', 'popup.js', 'popup.html', 'popup.css', 'i18n.js')
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $packagePath $file)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('manifest.' + $Browser + '.json')) -Destination (Join-Path $packagePath 'manifest.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '_locales') -Destination $packagePath -Recurse
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $packagePath 'LICENSE')
Compress-Archive -Path (Join-Path $packagePath '*') -DestinationPath $zipPath
Write-Output $packagePath
Write-Output $zipPath

