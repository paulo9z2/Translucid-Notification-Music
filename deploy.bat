@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
cd /d "%~dp0"

:: Garante git e gh no PATH
if exist "C:\Program Files\Git\cmd\git.exe" set "PATH=C:\Program Files\Git\cmd;C:\Program Files\Git\bin;%PATH%"
if exist "C:\Program Files\GitHub CLI\gh.exe" set "PATH=C:\Program Files\GitHub CLI;%PATH%"

set "REPO=paulo9z2/Translucid-Notification-Music"

echo ===== Translucid Notification Music Deploy =====

:: Versao (Enter = ultima + 1)
set "LATEST="
for /f "tokens=*" %%a in ('gh release list --repo %REPO% --limit 1 --json tagName --jq ".[0].tagName" 2^>nul') do set "LATEST=%%a"
set /p VERSION="Digite a versao (Enter = %LATEST% +1, ex: 1.0.3): "

if "%VERSION%"=="" (
    if "%LATEST%"=="" (
        set "VERSION=1.0.0"
    ) else (
        for /f "tokens=1,2,3 delims=v." %%a in ("%LATEST%") do (
            set /a "BUILD=%%c+1"
            set "VERSION=%%a.%%b.!BUILD!"
        )
    )
    echo Usando versao: %VERSION%  (ultima: %LATEST%)
)
if "%VERSION%"=="" set "VERSION=1.0.0"
echo Versao final: %VERSION%

:: Verifica gh auth
echo.
echo [0/5] Verificando autenticacao...
gh auth status >nul 2>&1
if %errorlevel% neq 0 (
    echo gh nao autenticado. Iniciando login...
    gh auth login -h github.com -w
    if %errorlevel% neq 0 (
        echo ERRO: falha no gh auth. Tente: gh auth login -h github.com -w
        pause
        exit /b 1
    )
)

echo.
echo [1/5] Build + ZIP + SHA256 (versao %VERSION%)...
powershell -ExecutionPolicy Bypass -File "Deploy.ps1" -Version "%VERSION%"
if %errorlevel% neq 0 (
    echo ERRO no Deploy.ps1
    pause
    exit /b 1
)

echo.
echo [2/5] Criando/atualizando release v%VERSION%...
gh release create "v%VERSION%" --repo %REPO% --title "Translucid v%VERSION%" --notes "Release automatica v%VERSION%" "Publish/Translucid.zip" "Publish/Translucid.zip.sha256" 2>nul
if %errorlevel% neq 0 (
    echo   release ja existe p/ v%VERSION% - apagando e recriando...
    gh release delete "v%VERSION%" --repo %REPO% --yes >nul 2>&1
    gh release create "v%VERSION%" --repo %REPO% --title "Translucid v%VERSION%" --notes "Release automatica v%VERSION%" "Publish/Translucid.zip" "Publish/Translucid.zip.sha256"
    if %errorlevel% neq 0 (
        echo AVISO: release/upload falhou. Faca manualmente em:
        echo   https://github.com/%REPO%/releases/new?tag=v%VERSION%
    )
)

echo.
echo [3/5] Git add + commit + push...
git add -A
git commit -m "Deploy v%VERSION%" 2>nul
git push >nul 2>&1

echo.
echo [4/5] Tag v%VERSION%...
git tag -f "v%VERSION%"
git push origin "v%VERSION%" --force >nul 2>&1

echo.
echo ===== Pronto! Translucid v%VERSION% publicado =====
echo   https://github.com/%REPO%/releases/tag/v%VERSION%
pause