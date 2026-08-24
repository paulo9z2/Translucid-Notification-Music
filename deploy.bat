@echo off
rem deploy.bat — atalho para o tooling C# nativo (Translucid.Deploy).
rem Uso: deploy.bat <versao> [--release]
if "%~1"=="" (
    echo Uso: deploy.bat ^<versao^> [--release]    ex.: deploy.bat 1.4.0 --release
    exit /b 1
)
dotnet run --project src\Translucid.Deploy -- %*
