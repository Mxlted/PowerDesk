<#
.SYNOPSIS
    Builds, tests, and publishes PowerDesk as a single self-contained executable into .\build\.

.DESCRIPTION
    1. Restores and builds the solution in Release.
    2. Runs the xUnit test suite (skip with -SkipTests).
    3. Publishes PowerDesk.exe (win-x64, self-contained, single-file) into .\build\ and writes a
       build-info.txt next to it.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipTests
    .\build.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDir = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$root     = $PSScriptRoot
$solution = Join-Path $root 'PowerDesk.slnx'
$project  = Join-Path $root 'PowerDesk\PowerDesk.csproj'

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

Step 'Restoring and building (Release)'
dotnet build $solution -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

if (-not $SkipTests) {
    Step 'Running tests'
    dotnet test $solution -c Release -nologo -v q --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)." }
}

Step "Publishing single-file $Runtime build to $OutputDir"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -nologo -v q -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

# Keep the output folder tidy: the app is a single exe; drop stray symbol files if any slipped through.
Get-ChildItem $OutputDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $OutputDir 'PowerDesk.exe'
if (-not (Test-Path $exe)) { throw "Publish produced no PowerDesk.exe in $OutputDir." }

$ver  = (Get-Item $exe).VersionInfo.ProductVersion
$size = '{0:N1} MB' -f ((Get-Item $exe).Length / 1MB)
$sha  = (Get-FileHash $exe -Algorithm SHA256).Hash
@"
PowerDesk $ver ($Runtime, self-contained single file)
Built:   $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
Size:    $size
SHA256:  $sha

Run PowerDesk.exe directly. No installer or .NET runtime required.
Data is stored under %LocalAppData%\PowerDesk\.
"@ | Set-Content (Join-Path $OutputDir 'build-info.txt') -Encoding UTF8

Copy-Item (Join-Path $root 'LICENSE') $OutputDir -Force
Copy-Item (Join-Path $root 'README.md') $OutputDir -Force

Step "Done: $exe ($size, v$ver)"
