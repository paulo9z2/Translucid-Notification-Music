@echo off
rem deploy.bat — orquestra o deploy 100% sem PowerShell.
rem Uso: deploy.bat [versao]   (ex.: deploy.bat 1.4.0)
setlocal
cd /d "%~dp0"

if "%~1"=="" (
    echo Uso: deploy.bat ^<versao^>    ex.: deploy.bat 1.4.0
    exit /b 1
)

echo ===== Translucid %~1 — Deploy =====

echo [1/4] Build + ZIP + SHA256...
dotnet run --file scripts\build-deploy.cs -- %~1
if errorlevel 1 (
    echo ERRO no build-deploy
    exit /b 1
)

echo [2/4] Release v%~1...
gh release create "v%~1" --title "Translucid v%~1" --generate-notes "Publish\Translucid.zip" "Publish\Translucid.zip.sha256"
if errorlevel 1 (
    echo   release ja existe - apagando e recriando...
    gh release delete "v%~1" --yes
    gh release create "v%~1" --title "Translucid v%~1" --generate-notes "Publish\Translucid.zip" "Publish\Translucid.zip.sha256"
    if errorlevel 1 (
        echo AVISO: crie manualmente em https://github.com/paulo9z2/Translucid-Notification-Music/releases/new?tag=v%~1
        exit /b 1
    )
)

echo [3/4] Git commit + push...
git add -A src DOSSIER.md
git commit -m "Deploy v%~1" --allow-empty
git push origin main

echo [4/4] Tag v%~1...
git tag -f "v%~1"
git push origin "v%~1" --force

echo.
echo ===== Pronto! Translucid v%~1 publicado =====
echo https://github.com/paulo9z2/Translucid-Notification-Music/releases/tag/v%~1
