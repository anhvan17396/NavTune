<#
.SYNOPSIS
    Builds NavTune for Windows: runs the tests, publishes a self-contained single-file NavTune.exe,
    zips it, and builds the installer when Inno Setup is available.

.EXAMPLE
    ./build.ps1                     # win-x64, tests, zip and installer
    ./build.ps1 -Runtime win-arm64  # for ARM PCs
    ./build.ps1 -SkipTests -NoInstaller
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-Tool([string[]]$candidates) {
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return (Resolve-Path $candidate).Path }
    }
    return $null
}

# .NET 10 SDK: on PATH, or the copy installed next to the repository by dotnet-install.ps1.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
$sdkOk = $dotnet -and ((& $dotnet --list-sdks 2>$null) -match '^10\.')
if (-not $sdkOk) {
    $dotnet = Find-Tool @("$root\..\..\.tools\dotnet\dotnet.exe", "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")
    if (-not $dotnet) { throw '.NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0' }
}

$version = ([xml](Get-Content "$root\src\NavTune\NavTune.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "NavTune-$version-$Runtime"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
Write-Host "NavTune $version for $Runtime (dotnet: $dotnet)" -ForegroundColor Cyan

if (-not $SkipTests) {
    & $dotnet test "$root\tests\NavTune.Tests\NavTune.Tests.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish "$root\src\NavTune\NavTune.csproj" -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item "$root\README.md" "$out\README.md"
Copy-Item "$root\THIRD-PARTY-NOTICES.txt" "$out\THIRD-PARTY-NOTICES.txt"

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Portable: $zip" -ForegroundColor Green

if (-not $NoInstaller) {
    $iscc = Find-Tool @($env:ISCC, "$root\..\..\.tools\innosetup\pkg\tools\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
    if (-not $iscc) {
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found: skipping the installer. Set $env:ISCC or install Inno Setup.'
    }
    elseif ($Runtime -ne 'win-x64') {
        Write-Warning 'The installer is built for win-x64 only.'
    }
    else {
        & $iscc /Qp "/DAppVersion=$version" "/DSourceExe=$out\NavTune.exe" "/DOutputDir=$dist" "$root\installer\NavTune.iss"
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
        Write-Host "Installer: $(Join-Path $dist "NavTune-Setup-$version.exe")" -ForegroundColor Green
    }
}
