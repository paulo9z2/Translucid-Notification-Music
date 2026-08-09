param(
    [Parameter(Mandatory=$false)]
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
if (-not $Root) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }

# Build self-contained single-file .exe
Write-Host "[Deploy] Build Release single-file..."
$build = Start-Process -FilePath "dotnet" -ArgumentList @(
    "publish", "src/Translucid.App",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-o", "dist\Translucid"
) -NoNewWindow -Wait -PassThru
if ($build.ExitCode -ne 0) {
    throw "Build falhou (exit $($build.ExitCode))"
}

# Prepara Publish/
$pubDir = Join-Path $Root "Publish"
if (-not (Test-Path $pubDir)) { New-Item -ItemType Directory -Path $pubDir -Force | Out-Null }

# Copia o exe publicado (e, se existir, README + scripts para zip completo)
$distDir = Join-Path $Root "dist\Translucid"
$zipName = "Translucid.zip"
$zipPath = Join-Path $pubDir $zipName
$shaPath = "$($zipPath).sha256"

# Cria o zip só com o executavel + README + scripts
$items = @(
    (Get-Item (Join-Path $distDir "Translucid.exe")),
    (Get-Item (Join-Path $Root "README.md")),
    (Get-Item (Join-Path $Root "scripts\setup-terminal.ps1"))
)

Write-Host "[Deploy] Criando $zipName..."
Compress-Archive -Path ($items | ForEach-Object { $_.FullName }) -DestinationPath $zipPath -Force

# Gera SHA256
Write-Host "[Deploy] Gerando SHA256..."
$sha256 = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
Set-Content -Path $shaPath -Value "$sha256  $zipName"

Write-Host "===== Deploy concluido (v$Version) ====="
Write-Host "Assets: $zipPath"
Write-Host "Hash:  $shaPath"
