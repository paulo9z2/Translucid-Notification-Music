@echo off
chcp 65001 >nul
cd /d "%~dp0"

:: Garante git e gh no PATH
if exist "C:\Program Files\Git\cmd\git.exe" set "PATH=C:\Program Files\Git\cmd;C:\Program Files\Git\bin;%PATH%"
if exist "C:\Program Files\GitHub CLI\gh.exe" set "PATH=C:\Program Files\GitHub CLI;%PATH%"

echo ===== Translucid Notification Music Deploy =====

:: Pede a versao (Enter = ultima)
set /p VERSION="Digite a versao (Enter = ultima, ex: 1.0.0): "
if "%VERSION%"=="" (
    for /f "tokens=*" %%a in ('gh release list --repo paulo9z2/translucid-notification-music --limit 1 --json tagName --jq ".[0].tagName" 2^>nul') do set "TAG=%%a"
    set "VERSION=%TAG:v=%"
    if "%VERSION%"=="" (
        echo AVISO: nao encontrou ultima release. Usando 1.0.0
        set "VERSION=1.0.0"
    ) else (
        echo Usando ultima versao: %VERSION%
    )
)

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
echo [2/5] Criando release v%VERSION% e upload assets...
gh release create "v%VERSION%" --title "Translucid v%VERSION%" --notes "Release automatica v%VERSION%" "Publish/Translucid.zip" "Publish/Translucid.zip.sha256" 2>nul
if %errorlevel% neq 0 (
    echo AVISO: release/upload falhou. Acesse:
    echo   https://github.com/paulo9z2/translucid-notification-music/releases/new?tag=v%VERSION%
)

echo.
echo [3/5] Git add + commit...
git add -A
git commit -m "Deploy v%VERSION%" 2>nul

echo.
echo [4/5] Git push...
git push >nul 2>&1

echo.
echo [5/5] Tag v%VERSION%...
git tag -f "v%VERSION%"
git push origin "v%VERSION%" --force >nul 2>&1

echo.
echo ===== Pronto! Translucid v%VERSION% publicado =====
echo Assets em Publish/ e no GitHub.
pause
