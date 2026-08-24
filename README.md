# Translucid Notification Music

Widget interativo de música + desktop translúcido estilo **Arch Linux / KDE Plasma** para Windows 11.

## Funcionalidades
- **Widget de música transparente** com blur real (SMTC) — lê o que está tocando no YouTube Music, Spotify, apps nativos, etc.
- **Letras sincronizadas** (LRCLIB) com destaque estilo Spicy Lyrics — clique numa linha para pular a música para aquele momento.
- **Fundo degradê na cor da capa**, transição de capa animada, volume por aplicativo no scroll.
- **Auto-atualização**: pill azul "update" aparece quando há release novo; um clique reinstala sozinho.
- Widget movível / bloqueável (clique direito trava/desbloqueia), minimiza para a bandeja.
- Bordas arredondadas nativas.

## Rodar o projeto
```bash
dotnet run --project src/Translucid.App
```

## Deploy (100% C#, sem PowerShell)
```bash
dotnet run --file scripts/build-deploy.cs -- 1.4.0   # build + zip + sha256
deploy.bat 1.4.0                                      # acima + release no GitHub + tag
```

## Notas
- A música é capturada via `Windows.Media.Control` (SMTC) — funciona com qualquer player que registre mídia no sistema.
- As letras vêm da API pública [LRCLIB](https://lrclib.net).
- Configurações em `%LOCALAPPDATA%\Translucid\ui.json`; autostart em `HKCU\...\Run`.
