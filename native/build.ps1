param(
    [ValidateSet('x86','x64')][string]$Architecture = 'x64',
    [Parameter(Mandatory = $true)][string]$CompilerPath,
    [Parameter(Mandatory = $true)][string]$CurlIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$CurlImportLibrary,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$options = @('-shared','-O2','-Wall','-Wextra','-D_WIN32_WINNT=0x0601','-DWINVER=0x0601','-static-libgcc',
    '-Wl,--major-subsystem-version,6,--minor-subsystem-version,1',('-I' + [IO.Path]::GetFullPath($CurlIncludeDirectory)))
if ($Architecture -eq 'x86') { $options += '-m32' }
& $CompilerPath @options (Join-Path $PSScriptRoot 'qcurl.c') ([IO.Path]::GetFullPath($CurlImportLibrary)) '-o' (Join-Path $outputPath 'qcurl.dll')
if ($LASTEXITCODE -ne 0) { throw 'Native curl bridge compilation failed.' }
Write-Output (Join-Path $outputPath 'qcurl.dll')
