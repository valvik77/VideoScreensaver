[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactsDirectory = Join-Path $repositoryRoot "artifacts"
$publishDirectory = Join-Path $artifactsDirectory "publish"
$installerDirectory = Join-Path $artifactsDirectory "installer"
$projectPath = Join-Path $repositoryRoot "src\VideoScreensaver\VideoScreensaver.csproj"
$installerScript = Join-Path $PSScriptRoot "VideoScreensaver.iss"

$msbuildCandidates = @(
    "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) {
    throw "No se encontró MSBuild de Visual Studio. Instala Visual Studio o Build Tools con las herramientas de Windows App SDK."
}

$uninstallRegistryPaths = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
)
$innoCompiler = Get-ItemProperty $uninstallRegistryPaths -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like "Inno Setup 7*" -and $_.InstallLocation } |
    ForEach-Object { Join-Path $_.InstallLocation "ISCC.exe" } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $innoCompiler) {
    throw "No se encontró Inno Setup 7 (ISCC.exe). Instálalo antes de crear el instalador."
}

if (Test-Path $publishDirectory) {
    Get-ChildItem -Path $publishDirectory -Force | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDirectory, $installerDirectory | Out-Null

& $msbuild $projectPath /restore /t:Publish "/p:Configuration=$Configuration" /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true "/p:PublishDir=$publishDirectory\"
if ($LASTEXITCODE -ne 0) {
    throw "La publicación de la aplicación falló con código de salida $LASTEXITCODE."
}

$application = Get-Item (Join-Path $publishDirectory "VideoScreensaver.exe")
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($application.FullName).FileVersion
if (-not $version) {
    throw "No se pudo determinar la versión de $($application.Name)."
}

$priFile = Join-Path $publishDirectory "VideoScreensaver.pri"
if (Test-Path $priFile) {
    Copy-Item -Path $priFile -Destination (Join-Path $publishDirectory "resources.pri") -Force
}

& $innoCompiler "/DMyAppVersion=$version" "/O$installerDirectory" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "La compilación de Inno Setup falló con código de salida $LASTEXITCODE."
}

$installer = Get-ChildItem -Path $installerDirectory -Filter "VideoScreensaver-Setup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $installer) {
    throw "Inno Setup terminó sin generar el instalador."
}

Write-Host "Instalador creado: $($installer.FullName)"
