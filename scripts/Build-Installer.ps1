[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactsDirectory = Join-Path $repositoryRoot "artifacts"
$publishDirectory = Join-Path $artifactsDirectory "publish"
$stubPublishDirectory = Join-Path $artifactsDirectory "publish-stub"
$installerDirectory = Join-Path $artifactsDirectory "installer"
$projectPath = Join-Path $repositoryRoot "src\VideoScreensaver\VideoScreensaver.csproj"
$stubProjectPath = Join-Path $repositoryRoot "src\ScreenSaverStub\ScreenSaverStub.csproj"
$installerScript = Join-Path $PSScriptRoot "VideoScreensaver.iss"

$vswhereCandidates = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
    "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$msbuild = $null
foreach ($vswhere in $vswhereCandidates) {
    $candidate = & $vswhere -latest -products * -version "[17.0,19.0)" -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin\MSBuild.exe" 2>$null |
        Select-Object -First 1
    if ($candidate -and (Test-Path $candidate)) {
        $msbuild = $candidate
        break
    }
}

if (-not $msbuild) {
    $msbuildCandidates = foreach ($majorVersion in "18", "17") {
        foreach ($programFilesDirectory in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
            foreach ($edition in "Enterprise", "Professional", "Community", "BuildTools") {
                if ($programFilesDirectory) {
                    Join-Path $programFilesDirectory "Microsoft Visual Studio\$majorVersion\$edition\MSBuild\Current\Bin\MSBuild.exe"
                }
            }
        }
    }
    $msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $msbuild) {
    throw "No se encontró MSBuild 17 ni 18. Instala Visual Studio o Build Tools con MSBuild y las herramientas de Windows App SDK."
}

Write-Host "MSBuild detectado: $msbuild"

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
if (Test-Path $stubPublishDirectory) {
    Get-ChildItem -Path $stubPublishDirectory -Force | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDirectory, $stubPublishDirectory, $installerDirectory | Out-Null

& $msbuild $projectPath /restore /t:Publish "/p:Configuration=$Configuration" /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true "/p:PublishDir=$publishDirectory\"
if ($LASTEXITCODE -ne 0) {
    throw "La publicación de la aplicación falló con código de salida $LASTEXITCODE."
}

& $msbuild $stubProjectPath /restore /t:Publish "/p:Configuration=$Configuration" /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true "/p:PublishDir=$stubPublishDirectory\"
if ($LASTEXITCODE -ne 0) {
    throw "La publicación del lanzador (stub) del protector de pantalla falló con código de salida $LASTEXITCODE."
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
