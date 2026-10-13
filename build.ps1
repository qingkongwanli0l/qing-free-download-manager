param(
    [ValidateSet('x86','x64')][string]$Architecture = 'x64',
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ReferenceDirectory = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8",
    [string]$EngineDirectory
)
$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$sourcePath = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
if (($outputPath.TrimEnd('\') + '\').StartsWith($sourcePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose an output directory outside the source directory.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $ReferenceDirectory 'mscorlib.dll'))) { throw 'Install the .NET Framework 4.8 Developer Pack or supply -ReferenceDirectory.' }
$dependencyCache = Join-Path ([IO.Path]::GetTempPath()) 'QingFDM-Build'
New-Item -ItemType Directory -Path $dependencyCache -Force | Out-Null
$natPackage = Join-Path $dependencyCache 'mono.nat.3.0.4.nupkg'
if (-not (Test-Path -LiteralPath $natPackage)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/mono.nat/3.0.4/mono.nat.3.0.4.nupkg' -OutFile $natPackage
}
if ((Get-FileHash -LiteralPath $natPackage -Algorithm SHA256).Hash -ne '35D3AAB94D8D68AB42BF4A75F9E63A6B08CE070C337FDD82C002780E0CF6FB83') { throw 'Mono.Nat package hash does not match version 3.0.4.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($natPackage)
try {
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('lib/netstandard2.0/Mono.Nat.dll'), (Join-Path $outputPath 'Mono.Nat.dll'), $true)
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('LICENSE.md'), (Join-Path $outputPath 'Mono.Nat-LICENSE.txt'), $true)
} finally { $archive.Dispose() }
$references = 'mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Security','System.Web.Extensions','System.Net.Http'
$options = @('/nologo','/noconfig','/nostdlib+','/langversion:5','/optimize+',"/platform:$Architecture")
foreach ($reference in $references) { $options += "/reference:$(Join-Path $ReferenceDirectory ($reference + '.dll'))" }
$options += "/reference:$(Join-Path $ReferenceDirectory 'Facades\netstandard.dll')", "/reference:$(Join-Path $outputPath 'Mono.Nat.dll')"
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compilerPath @options /target:winexe "/win32manifest:$(Join-Path $PSScriptRoot 'src\app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'assets\qing.ico')" "/resource:$(Join-Path $PSScriptRoot 'assets\qing.ico'),QingFDM.Icon" "/resource:$(Join-Path $PSScriptRoot 'assets\qing-active.ico'),QingFDM.ActiveIcon" "/out:$(Join-Path $outputPath 'QingFDM.exe')" @sources
if ($LASTEXITCODE -ne 0) { throw 'Desktop compilation failed.' }
& $compilerPath @options /target:exe /define:NATIVE_HOST "/win32icon:$(Join-Path $PSScriptRoot 'assets\qing.ico')" "/out:$(Join-Path $outputPath 'QingFDM.NativeHost.exe')" (Join-Path $PSScriptRoot 'src\Program.cs') (Join-Path $PSScriptRoot 'src\NativeBridge.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser host compilation failed.' }
$runtimeConfig = '<?xml version="1.0"?><configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup></configuration>'
[IO.File]::WriteAllText((Join-Path $outputPath 'QingFDM.exe.config'), $runtimeConfig)
[IO.File]::WriteAllText((Join-Path $outputPath 'QingFDM.NativeHost.exe.config'), $runtimeConfig)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $outputPath
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'README.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $outputPath }
$pluginOutput = Join-Path $outputPath 'plugins\qing\yt_dlp_plugins\postprocessor'
New-Item -ItemType Directory -Path $pluginOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'plugins\qing\yt_dlp_plugins\postprocessor\origin_scope.py') -Destination $pluginOutput -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'plugins\qing\yt_dlp_plugins\postprocessor\animation_preview.py') -Destination $pluginOutput -Force
foreach ($browser in 'chromium','firefox') {
    $browserOutput = Join-Path $outputPath ('browser\' + $browser)
    New-Item -ItemType Directory -Path $browserOutput -Force | Out-Null
    foreach ($file in 'background.js','content.js','popup.js','popup.html','popup.css','i18n.js') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('browser\' + $file)) -Destination $browserOutput -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('browser\manifest.' + $browser + '.json')) -Destination (Join-Path $browserOutput 'manifest.json') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'browser\_locales') -Destination $browserOutput -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $browserOutput -Force
}
if ($EngineDirectory) {
    $engineOutput = Join-Path $outputPath 'engines'
    New-Item -ItemType Directory -Path $engineOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $EngineDirectory | Copy-Item -Destination $engineOutput -Recurse -Force
}
Write-Output (Join-Path $outputPath 'QingFDM.exe')
