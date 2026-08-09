# Translucid - injeta um perfil translucido estilo Arch (Catppuccin Mocha + Acrylic)
# no Windows Terminal. Faz backup do settings.json antes de mexer.
$ErrorActionPreference = "Stop"

$packagesPath = Join-Path $env:LOCALAPPDATA "Packages"
$profile = $null

if (Test-Path (Join-Path $packagesPath "Microsoft.WindowsTerminal_8wekyb3d8bbwe")) {
    $profile = Join-Path $packagesPath "Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json"
}
elseif (Test-Path (Join-Path $packagesPath "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe")) {
    $profile = Join-Path $packagesPath "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json"
}
else {
    $unpackaged = Join-Path $env:LOCALAPPDATA "Microsoft\Windows Terminal\settings.json"
    if (Test-Path $unpackaged) { $profile = $unpackaged }
}

if (-not $profile) {
    Write-Host "Windows Terminal nao encontrado. Instale pela Microsoft Store e rode de novo." -ForegroundColor Yellow
    exit 1
}

Copy-Item $profile "$profile.bak" -Force
Write-Host "Backup em: $profile.bak"

$json = Get-Content $profile -Raw | ConvertFrom-Json

$guid = "{5f2f2c9d-2d6f-4a0c-9e1a-a1b2c3d4e5f6}"  # GUID fixo do perfil Translucid

$translucidProfile = [pscustomobject]@{
    guid          = $guid
    name          = "Translucid (Arch)"
    commandline   = "powershell.exe"
    useAcrylic    = $true
    opacity       = 65
    acrylicOpacity = 65
    background    = "#1E1E2E00"          # reposo sem chumbo, acrylic cuida do blur
    colorScheme   = "Catppuccin Mocha"
    font          = [pscustomobject]@{ face = "Cascadia Mono"; size = 11 }
    cursorShape   = "underscore"
    padding       = "8,8,8,8"
    initialRows   = 30
    initialCols   = 90
}

$scheme = [pscustomobject]@{
    name       = "Catppuccin Mocha"
    foreground = "#CDD6F4"
    background = "#1E1E2E"
    cursorColor = "#F5E0DC"
    selectionBackground = "#585B70"
    black        = "#45475A"
    red          = "#F38BA8"
    green        = "#A6E3A1"
    yellow       = "#F9E2AF"
    blue         = "#89B4FA"
    purple       = "#F5C2E7"
    cyan         = "#94E2D5"
    white        = "#BAC2DE"
    brightBlack  = "#585B70"
    brightRed    = "#F38BA8"
    brightGreen  = "#A6E3A1"
    brightYellow = "#F9E2AF"
    brightBlue   = "#89B4FA"
    brightPurple = "#F5C2E7"
    brightCyan   = "#94E2D5"
    brightWhite  = "#A6ADC8"
}

if (-not $json.schemes) { $json | Add-Member -NotePropertyName schemes -NotePropertyValue @() }
$json.schemes = @($json.schemes | Where-Object { $_.name -ne $scheme.name }) + $scheme

$list = $json.profiles.list
$list = @($list | Where-Object { $_.guid -ne $guid }) + $profiles
$json.profiles.list = $list

if ($json.profiles.defaultProfile) { $json.profiles.defaultProfile = $guid }

$json | ConvertTo-Json -Depth 10 | Set-Content $profile -Encoding utf8
Write-Host "Perfil 'Translucid (Arch)' instalado e definido como padrao. (Transparency no WT: Ctrl+Shift+Scroll)"