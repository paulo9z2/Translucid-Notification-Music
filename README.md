# Translucid Notification Music

Widget interativo de música + desktop translúcido estilo **Arch Linux / KDE Plasma** para Windows 11.

## Funcionalidades
- **Widget de música transparente** com blur real (SMTC) — lê o que está tocando no YouTube Music, Spotify, apps nativos, etc.
- **Terminal translúcido** estilo Arch via perfil do Windows Terminal.
- Widget movível / bloqueável (clique direito trava/desbloqueia).
- Bordas arredondadas nativas.

## Rodar o projeto
```bash
dotnet run --project src/Translucid.App
```

## Publicar .exe completo
```bash
dotnet publish src/Translucid.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Configurar terminal
```powershell
powershell -ExecutionPolicy Bypass -File scripts/setup-terminal.ps1
```

## Notas
- A música é capturada via `Windows.Media.Control` (SMTC) — funciona com qualquer player que registre mídia no sistema.
- O terminal usa o perfil `Translucid (Arch)` (Catppuccin Mocha + Acrylic).
