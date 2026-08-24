# Translucid Notification Music — Dossiê Técnico Completo

> Documento de referência sobre o aplicativo **Translucid**: o que é, como funciona,
> arquitetura, cada arquivo do código-fonte, integrações com o Windows, e como
> compilar e distribuir.
>
> Estado do documento: atualizado conforme o código-fonte atual.

---

## 1. Visão geral

**Translucid Notification Music** é um widget flutuante de desktop para Windows
(WinForms/WPF, .NET 9) que aparece no canto da tela mostrando a música/vídeo em
reprodução no sistema — Spotify, YouTube Music no navegador, apps nativos, etc.

Em vez de ser mais uma janela comum, o widget é pensado como **camada de fundo**:
- Fica **sempre atrás de todas as janelas** (configurável);
- Tem **cantos arredondados nativos** e **efeito Acrylic** (blur translúcido) do Windows;
- Não aparece na taskbar nem no Alt+Tab (é uma janela "tool");
- Ao fechar, **minimiza para a bandeja** (ícones ocultos) — não morre.

Além do controle de mídia básico (play/pause/próxima/anterior, tempo decorrido),
ele tem um visual "vivo": o fundo do widget vira um **degradê translúcido na cor
da capa da música** (transição suave), a capa **desliza** quando troca de faixa/app,
o **scroll do mouse controla o volume do aplicativo reproduzindo**, e há modo de
**letras sincronizadas** estilo "Spicy Lyrics" (destaque da linha atual com glow).

---

## 2. Funcionalidades em detalhe

### 2.1 Rastreamento de mídia (SMTC)
- Usa a API pública do Windows `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`.
- Detecta a **sessão de mídia atual** do sistema — funciona com Spotify (desktop),
  YouTube Music/YouTube em Chrome/Edge, players nativos, apps UWP, etc.
- Reage em tempo real:
  - `MediaPropertiesChanged` → troca de faixa/título/artista/capa;
  - `PlaybackInfoChanged` → play/pause, capacidades dos botões;
  - `TimelinePropertiesChanged` → posição/duração.
- Quando não há sessão, publica um estado `Idle` ("Nada tocando").

### 2.2 Controles
- Botões **Anterior / Play-Pause / Próxima** (ícones Segoe MDL2 Assets).
- Botões são habilitados/desabilitados conforme o que o app de origem permite.
- O ícone do play troca entre ▶ e ⏸ conforme o estado.

### 2.3 Capa e transições
- A capa do streaming é baixada da sessão de mídia e mostrada em um quadrado
  arredondado de 86×86.
- **Transição "slide"**: quando a capa muda (troca de faixa ou de aplicativo),
  a capa antiga **desliza para a esquerda** enquanto a nova **entra pela direita**
  (400–420 ms, easing quadrático) — implementada com duas camadas `Image`
  alternadas e `TranslateTransform` animado.
- **Capa ausente** → fade-out suave e fundo volta ao tom escuro padrão.

### 2.4 Fundo adaptativo ("degradê na cor da capa")
- Ao receber uma capa nova, o app extrai **3 cores** dela (faixas horizontais:
  topo, meio e base) decodificando a imagem em miniatura (16 px de largura) e
  tirando a média RGB de cada faixa.
- Essas cores viram paradas de um `LinearGradientBrush` vertical que cobre o
  widget **por baixo do conteúdo** com **transparência** (alphas 0x40/0x34/0x2A),
  preservando a translucidez do Acrylic.
- A mudança de cor é **animada**: um `DispatcherTimer` de ~33 ms interpola as
  cores atuais até as novas em ~1,4 s com easing (smoothstep).
- Sem capa → degradê escuro padrão (#0A0B0D).

### 2.5 Volume por aplicativo (scroll do mouse)
- Rolar o scroll sobre o widget **sobe/desce o volume só do app que está
  reproduzindo** (ex.: mexe no volume do Spotify, não no volume geral do Windows).
- Implementado via **CoreAudio** (NAudio): enumera as sessões de áudio do
  dispositivo de saída padrão e encontra a sessão do processo dono da mídia
  (match pelo nome do processo extraído do AUMID — `chrome.exe`, `Spotify.exe`, ...).
- Se não houver match por nome (apps empacotados), usa como fallback a única
  sessão não-sistema disponível.
- Passo de ajuste: **3% por notch** do scroll.
- Durante o ajuste aparece um **overlay de porcentagem** ("56%") central, que
  fade-out em ~1 s.
- Regra de exceção: com as letras expandidas, se o cursor estiver sobre o painel
  de letras, o scroll rola a letra (não mexe no volume).

### 2.6 Letras sincronizadas ("Spicy Lyrics")
- Habilitada em **Configurações** (tray). Quando ligada, aparece uma **seta
  (chevron) no canto inferior esquerdo** do widget.
- Clicar na seta **expande o widget para baixo** (altura animada 172 → 408 px)
  e revela o painel de letras.
- As letras vêm da API pública **LRCLIB** (`https://lrclib.net`), a mesma fonte
  de plugins como o Spicetify Lyrics:
  1. `GET /api/get?track_name=...&artist_name=...` (retorno único);
  2. se 404 (múltiplas versões), tenta `GET /api/search?...` e pega a primeira
     faixa com letra sincronizada.
  3. Resposta em **LRC** (`[mm:ss.xx] linha`) é parseada em `LyricLine[]`.
- Cache em memória por faixa (título+artista) para não repetir chamadas.
- **Sincronização**: a cada tick (500 ms) calcula a linha atual pela posição da
  mídia (+300 ms de offset); a linha ativa fica **maior, branca, com glow**
  (DropShadowEffect) — as demais ficam apagadas (estilo Spicy Lyrics).
- O painel **auto-rola** mantendo a linha ativa centralizada.
- Estados de UI: "Buscando letras…", "Sem letras encontradas para esta música",
  "Ponha uma música tocando para ver a letra".
- Desligar a opção recolhe o painel e limpa o estado; a seta some.

### 2.7 Sempre atrás das janelas (camada de fundo)
- Configuração **"Sempre atrás das janelas"**, **ativada por padrão**.
- Um `DispatcherTimer` de **1 s** chama `SetWindowPos(hwnd, HWND_BOTTOM, ...)`
  com `SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE` — mantém o widget abaixo de
  qualquer janela/App mesmo depois que o Windows re-ordena camadas.
- Desativado → comportamento normal de janela.

### 2.8 Posição, trava e gestos
- **Posição**: posição inicial = canto superior direito da área de trabalho
  (16 px de borda); depois, a posição **é salva e restaurada** entre sessões.
- **Trava**: clique direito alterna travar/desbloquear (ícone de cadeado muda;
  borda fica azul quando desbloqueado; cursor vira "mover").
- **Arrastar**: com o widget desbloqueado, arraste pelo corpo (menos botões).
- **Fechar (✕)**: esconde para a bandeja (com balão informativo uma única vez).
- **Resize**: não tem (tamanho fixo 440×172; 408 com letras).

### 2.9 Bandeja (ícones ocultos)
- Ícone na bandeja com menu:
  - **Mostrar widget** / **Esconder widget**;
  - **Configurações…** (abre a janela de preferências);
  - **Sair** (encerra de verdade — `ShutdownMode.OnExplicitShutdown`, ou seja,
    fechar janelas não mata o app; só o "Sair" encerra).
- Duplo clique no ícone alterna mostrar/esconder.

### 2.10 Janela de Configurações
- Abre perto do widget (deslocada 48 px), arrastável pelo corpo, com botão **✕**.
- Três opções com toggles (estilo switch):
  1. **Iniciar com o Windows** — grava/remove chave no registro
     `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (valor `Translucid`);
  2. **Sempre atrás das janelas** — comportamento 2.7;
  3. **Letras sincronizadas** — comportamento 2.6.
- Mudanças são persistidas na hora e aplicadas em tempo real no widget
  (evento `AppSettings.Changed`).

---

## 3. Arquitetura

Dois projetos .NET resolvidos pelo `Translucid.slnx`

```
translucid windows/
├── Translucid.slnx                      # solução (.NET 9)
├── dist/Translucid/                     # saída publicada (com DLLs + exe)
├── scripts/
│   ├── setup-terminal.ps1               # perfil Translucid no Windows Terminal
│   └── make-icon.ps1                    # gera app.ico a partir de PNG
└── src/
    ├── Translucid.Core/                 # lógica independente de UI
    └── Translucid.App/                  # WPF: janelas, bandeja, animações
```

### 3.1 Translucid.Core (biblioteca)
- **`MediaTracker.cs`** — wrapper da sessão de mídia SMTC. Publica `MediaUpdate`
  (snapshot: título, artista, álbum, app, processo, thumbnail, posição/duração,
  estado, capacidades). Contém `MediaUpdate.Idle` como "nada tocando".
  - Otimização: a thumbnail **só é relida quando a faixa muda** (cache por
    `título|artista|álbum`), evitando re-decodificar a cada evento de posição.
  - Resolve o nome amigável do app (via `AppInfo`) e o nome do processo (só
    quando o AUMID termina em `.exe`).
  - Expõe `TogglePlayPauseAsync`, `NextAsync`, `PreviousAsync`.
- **`AppSettings.cs`** — configurações persistentes (classe singleton `Current`):
  - Arquivo: `%LOCALAPPDATA%\Translucid\ui.json` (posição, trava, letras,
    sempre-atrás). `HasSavedPosition` indica se já havia posição salva.
  - Registro: autostart em `HKCU\...\Run` (`IsAutoStartEnabled`/`SetAutoStart`).
  - Evento **`Changed`** — disparado ao salvar; o widget escuta para aplicar
    configurações em tempo real. Nunca quebra o app em erro de I/O (try/catch).
- **`VolumeMixer.cs`** — volume **por aplicativo** via **NAudio (CoreAudio)**:
  - Abre o dispositivo de saída padrão (`MMDeviceEnumerator` →
    `DataFlow.Render`/`Role.Multimedia`) e lista as **sessões de áudio**.
  - Match pelo nome do processo (ignoreCase, com/ sem `.exe`); queda para a
    única sessão não-sistema se não houver match.
  - `Adjust(processName, steps)` aplica ±3%/notch no `SimpleAudioVolume`;
    `Get(processName)` lê o volume atual (0..1).
- **`LyricsService.cs`** — cliente LRCLIB:
  - `GetAsync(title, artist)` → `LyricLine[]?` (tempo + texto).
  - Caminho `/api/get` → fallback `/api/search`; parse de LRC com regex
    `\[mm:ss(.fff)\]`, ignorando tags vazias/meta, deduplicando tempos iguais,
    ordenando por tempo. User-Agent `Translucid/1.0`.
- **`NativeFx.cs`** (`DesktopFx`) — efeitos nativos de janela:
  - `EnableAcrylic` — Acrylic (blur) via `SetWindowCompositionAttribute`
    (AccentPolicy, state 4, tint 0x88000000). Retorna se o DWM aceitou.
  - `TryCornerRounding` — cantos arredondados via DWM
    (`DWMWA_WINDOW_CORNER_PREFERENCE`).
  - `RoundCorners` — fallback com região (`CreateRoundRectRgn`/`SetWindowRgn`).
  - `HideFromAltTab` — estilo `WS_EX_TOOLWINDOW`.
  - `PlaceBelowWindows` — `SetWindowPos(hwnd, HWND_BOTTOM, ...)`, sem mover/
    redimensionar/ativar; chamado periodicamente para manter a camada de fundo.
- **`Class1.cs`** — resquício do template inicial; **não usado** (pode ser
  removido).

### 3.2 Translucid.App (WPF + WinForms)
- **`App.xaml(.cs)`** — bootstrap:
  - `ShutdownMode.OnExplicitShutdown` (só "Sair" encerra o processo);
  - Cria `NotifyIcon` (bandeja), `ContextMenuStrip` (Mostrar/Esconder,
    Configurações…, Sair), duplo-clique; mostra balão quando vai para a bandeja;
  - `OpenSettings()` → `SettingsWindow.ShowOrFocus()`.
- **`MainWindow.xaml(.cs)`** — o widget:
  - XAML: estrutura em 3 linhas (cabeçalho/capa+texto+botões, barra de
    progresso + chevron de letras, painel de letras), `GradientOverlay`
    (degradê atrás de tudo), duas camadas de capa (`CoverImageA/B` com
    `TranslateTransform`), overlay de volume, chevron com `RotateTransform`.
  - Code-behind: 4 timers — `_tick` (500 ms: posição + sincronização de
    letras), `_palette` (33 ms: lerp das cores do fundo), `_bottom` (1 s:
    manter abaixo de tudo), e timers efêmeros do overlay de volume.
  - Animações com `DoubleAnimation` + `QuadraticEase` (capa, altura das
    letras, rotação do chevron, opacidade).
  - Fechar → cancela `Closing` (se não foi o "Sair"), esconde e avisa a bandeja.
- **`SettingsWindow.xaml(.cs)`** — janela de preferências (arrastável com ✕),
  acrylic, 3 toggles; `ShowOrFocus` garante instância única; posiciona perto do
  widget.
- **`app.ico`** — ícone do app/tray (gerado por `scripts/make-icon.ps1`).

### 3.3 Fluxo de dados principal

```
Sessão SMTC (Spotify, Chrome...)
   └─ MediaTracker (eventos: propriedades/playback/timeline)
        └─ MediaUpdate (snapshot)
             ├─ MainWindow.OnMediaUpdated
             │    ├─ textos (título/artista/app)
             │    ├─ capa: slide entre 2 camadas + extração de paleta
             │    ├─ botões play/prev/next habilitados
             │    ├─ posição/duração (RenderPosition + _tick)
             │    └─ letras: MaybeFetchLyrics → LyricsService (LRCLIB)
             └─ Scroll do mouse → VolumeMixer (CoreAudio) → overlay %
```

---

## 4. Detalhes técnicos por arquivo (referência rápida)

| Arquivo | Responsabilidade | Tecnologias |
|---|---|---|
| `src/Translucid.Core/MediaTracker.cs` | Sessão de mídia do Windows | `Windows.Media.Control` (SMTC), `Windows.ApplicationModel.AppInfo` |
| `src/Translucid.Core/NativeFx.cs` | Efeitos de janela nativos | `user32.dll`, `dwmapi.dll`, `gdi32.dll` (P/Invoke) |
| `src/Translucid.Core/AppSettings.cs` | Persistência + configurações | `System.Text.Json`, `Microsoft.Win32.Registry` |
| `src/Translucid.Core/VolumeMixer.cs` | Volume por app | NAudio `CoreAudioApi` |
| `src/Translucid.Core/LyricsService.cs` | Letras sincronizadas | LRCLIB API (HTTP), regex LRC |
| `src/Translucid.App/MainWindow.xaml` | Layout do widget | XAML (WPF) |
| `src/Translucid.App/MainWindow.xaml.cs` | Lógica/animações do widget | WPF `DispatcherTimer`, `DoubleAnimation` |
| `src/Translucid.App/SettingsWindow.xaml(.cs)` | Janela de preferências | XAML/WPF |
| `src/Translucid.App/App.xaml.cs` | Bandeja + ciclo de vida | `NotifyIcon` (WinForms), `ContextMenuStrip` |
| `src/Translucid.App/AssemblyInfo.cs` | Meta do tema WPF | — |
| `scripts/setup-terminal.ps1` | Perfil Translucid no Windows Terminal | PowerShell + JSON |
| `scripts/make-icon.ps1` | Gera `app.ico` multi-tamanho | PowerShell + System.Drawing |

### Pacotes NuGet
- **NAudio 2.2.1** (Translucid.Core) — apenas `CoreAudioApi` (sessões de áudio).
  Obs.: no 2.2.1 não existem `AudioSessionControl2`/`AudioSessionManager2`; a API
  expõe `AudioSessionControl` e `SessionCollection` com indexador.

### Dependências nativas (hidden)
- Acrylic: `SetWindowCompositionAttribute` (não declarado oficialmente; usado
  com AccentPolicy). Se falhar, o app usa fundo sólido translúcido escuro.
- Cantos: DWM `DWMWA_WINDOW_CORNER_PREFERENCE` (Win11) + região arredondada.
- `WS_EX_TOOLWINDOW` para sumir do Alt+Tab.
- `SetWindowPos(HWND_BOTTOM)` para a camada de fundo.

---

## 5. Configurações e persistência

| Onde | Formato | Conteúdo |
|---|---|---|
| `%LOCALAPPDATA%\Translucid\ui.json` | JSON | `Left`, `Top`, `Locked`, `LyricsEnabled`, `AlwaysOnBottom` |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | valor de string | `Translucid` → `"<caminho do exe>"` (autostart) |

O arquivo é gravado ao fechar/ocultar o widget e a cada mudança de configuração.
Erros de leitura/escrita são silenciosos (o app continua funcionando com
padrões).

---

## 6. Scripts

### `scripts/setup-terminal.ps1`
Injeta no **Windows Terminal** um perfil translúcido estilo "Arch":
- Perfil `Translucid (Arch)` (`powershell.exe`, Acrylic 65%, fonte Cascadia Mono 11,
  cursor underscore, padding 8).
- Esquema de cores **Catppuccin Mocha** (fundo #1E1E2E, texto #CDD6F4 etc.).
- Define o perfil como padrão. Faz **backup** do `settings.json` em `.bak`.
- OBS.: as linhas 75 use `$profiles` — variável inexistente (a intenção era
  `$list`). **Bom para correção futura** (o perfil ainda é adicionado porque
  `$profiles` é `$null` e o `@(...)` + `$null` não apaga a lista, mas está
  logicamente errado).

### `scripts/make-icon.ps1`
Converte uma PNG em `app.ico` multi-tamanho (16–256, PNG embutido) usando
System.Drawing; centraliza a imagem no canvas com alta qualidade.

---

## 7. Como compilar e publicar

Pré-requisitos: SDK .NET 9+ (o ambiente atual roda .NET 10 preview).

```powershell
# Publicar + montar pacote organizado + instalar na pasta de uso (recomendado)
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1

# Ou manualmente, passo a passo:
dotnet build Translucid.slnx -c Release
dotnet publish src\Translucid.App -c Release -r win-x64 -o dist\Translucid
# depois copiar dist\Translucid\* para o local de uso
```

O script `scripts\deploy.ps1` faz tudo: publica, move as DLLs para a subpasta
`lib\`, ajusta `deps.json` e `runtimeconfig.json` (probing path) e instala no
destino (padrão: `C:\Users\9z2.pj\Downloads\translucid windows`).

### Layout do pacote final

```
<destino>\
├── Translucid.exe               # atalho/executável
├── Translucid.dll               # assembly do app (obrigatório na raiz)
├── Translucid.deps.json
├── Translucid.runtimeconfig.json
└── lib\                         # dependências
    ├── Translucid.Core.dll
    ├── Microsoft.Windows.SDK.NET.dll
    ├── WinRT.Runtime.dll
    └── NAudio*.dll
```

> Notas técnicas:
> - O assembly do app (`Translucid.dll`) e o `deps.json` precisam ficar na raiz
>   junto do exe — o host .NET exige.
> - As demais DLLs ficam em `lib\`; o host as encontra via
>   `additionalProbingPaths` no `runtimeconfig.json` (caminho absoluto, pois o
>   host resolve caminhos relativos contra o CWD, não contra o exe) e pelos
>   `path: "."` no `deps.json` (o probing procura em `<probe>/<path>/<nome>`).
> - `PublishSingleFile` self-contained falha neste SDK preview
>   (`MSB4018/GenerateBundle`); em SDK estável (9.0.316 instalado) pode ser
>   usado para gerar um exe único ~195 MB.

Rodar: duplo clique em `Translucid.exe`. O ícone aparece na bandeja; o widget
abre no canto superior direito.

---

## 8. Instalação da cópia de uso (Downloads)

O usuário mantém a versão em uso em:
```
C:\Users\9z2.pj\Downloads\translucid windows\
```
Atualização: rodar `scripts\deploy.ps1` (mata o processo, reorganiza, copia e
relança). Manualmente: matar o processo `Translucid`, copiar `dist\Translucid\*`
para a pasta, relançar o exe. `Translucid.exe` fica hospedado na raiz; DLLs na
subpasta `lib\`.

---

## 9. Limitações e observações conhecidas

- **Progress bar é display-only** — ainda não há seek (clicar na barra não
  pula na música). Ideia já mapeada (usar `TryChangePlaybackPositionAsync`).
- **Volume por app**: o match de sessão usa o nome do processo do AUMID; apps
  empacotados (ex.: Spotify da Store) caem no fallback de "única sessão" — se
  houver várias sessões ativas, o volume pode ir para o app errado.
- **Acrylic** depende de comportamento não documentado do DWM (funciona em
  Win10/11); sem ele, fundo sólido translúcido.
- **LRCLIB**: exige internet; músicas sem LRC na base mostram a mensagem de
  ausência. Há cache em memória por faixa (não persiste entre sessões).
- `Class1.cs` morto; `setup-terminal.ps1` tem a variável `$profiles` trocada.
- `MainWindow` tem aviso de nullability CS8622 no handler `Window_Closing`
  (harmless).
- Não há multi-monitor por tela (a posição salva é absoluta em coordenadas de
  tela primária).
- Não há hotkeys globais próprias (os atalhos de mídia são do Windows).

---

## 10. Roadmap (ideias pesquisadas na web)

Referências de inspiração: Seelen UI (widgets desktop), plasmoid-spotify /
musik-plasmoid (KDE), kil0bit-system-monitor (Win11), WinState, GlintBar.

1. **Seek na barra de progresso** (clicar/arrastar → `TryChangePlaybackPositionAsync`).
2. **Visualizer de áudio** — onda/barras reagindo à música (WASAPI loopback,
   estilo kde-audio-visualizer / MusiK).
3. **Accent dinâmico mais forte** — cor dominante da capa pintando
   botões/barra de progresso (não só o fundo).
4. **Monitor de sistema** (CPU/RAM/rede, sparklines, sem admin).
5. **Relógio + calendário** desktop (estilo plasma).
6. **Hotkeys globais** e multi-monitor.
7. **Auto-update da cópia de Downloads**.
8. **Widgets empilháveis** (notificações, quick settings / flyout de volume).
9. **Buscar letra por fallback adicional** (ex.: letras não-sincronizadas
   mostradas estáticas; ou Genius).

---

## 11. Histórico recente (resumo das mudanças)

1. Base inicial: widget SMTC com acrylic, trava (clique direito), arraste,
   persistência de posição, bandeja.
2. **Fundo degradê na cor da capa** (extração 3 faixas + lerp 1,4 s translúcido).
3. **Slide de capa** (troca de faixa/app com animação esquerda/direita).
4. **Volume por app no scroll** (CoreAudio + overlay %).
5. **Letras sincronizadas** (LRCLIB, chevron, expansão 408 px, spicy effect).
6. **Configurações na bandeja** (autostart, letras, sempre-atrás) + aba
   arrastável com ✕ + fundo translúcido clicável.
7. **Sempre atrás das janelas** restaurado como configuração (timer 1 s →
   HWND_BOTTOM).
8. Correções de build em .NET preview (NAudio 2.2.1, ambiguidades
   System.Drawing/WinForms, publish framework-dependent).
9. **Correção do parser LRC (`LyricsService.ParseLrc`)** — o fatiamento da linha
   partia de `m.Index` (início da tag `[mm:ss.xx]`), incluindo a própria tag no
   texto extraído; como todo texto começava com `[`, o filtro de metadados
   descartava TODAS as linhas (só escapava a última linha sem `\n` à direita e
   refrões com tags múltiplas). Sintoma relatado por usuários: letra não
   aparecia, ou apareciam apenas algumas palavras soltas. Correção: fatiar a
   partir de `m.Index + m.Length`. Validado contra `/api/get`, `/api/search`
   (CRLF), multi-tags e arquivo sem newline final.
10. **Centralização real da linha ativa das letras (`MainWindow.SyncLyrics`)** —
    o scroll usava pitch fixo (`índice × 27 px − 101 px`), mas as linhas têm
    altura variável (a ativa usa fonte maior), então o desvio se acumulava e a
    linha destacada saía do centro do painel. Agora `CenterActiveLyric()` mede
    a posição REAL do container na árvore visual
    (`TransformToVisual(LyricsScroll)`), força `UpdateLayout()` antes de medir
    (o estilo da linha ativa acabou de mudar) e centraliza por
    `(viewport − altura da linha) / 2`. Constante `LyricPitch` removida.
11. **Painel de letras estilo apps de música** — comparado ao Spotify/Apple
    Music/Spicy Lyrics, faltavam 4 comportamentos, todos implementados:
    (a) rolagem **suave animada** entre linhas (~260 ms, QuadraticEase; a
    animação é liberada ao completar para não travar o scroll manual — fallback
    para salto seco se o runtime recusar animar `VerticalOffset`);
    (b) **spacers de 104 px** acima e abaixo da lista, para a primeira e última
    linha alcançarem o centro do painel (antes ficavam presas nas bordas);
    (c) **3 estados de opacidade**: linhas passadas bem apagadas (0x3D),
    futuras legíveis (0x8C), ativa branca com glow;
    (d) texto das letras **centralizado horizontalmente** (`TextAlignment`).
12. **Correção do recorte das letras expandidas** — sintoma: ao expandir o
    painel, só ~2 linhas de letra apareciam logo abaixo da barra de tempo; todo
    o resto ficava invisível. Causa: `DesktopFx.RoundCorners` usa
    `SetWindowRgn`, e a região de recorte fica FIXA no tamanho da janela no
    momento em que é aplicada (`OnSourceInitialized`, janela com 172 px). Ao
    animar a altura para 408 px, tudo abaixo dos 172 px originais continuava
    fora da região e era cortado pelo DWM. Correção: handler `Window_SizeChanged`
    reaplica `RoundCorners(hwnd, 14)` a cada mudança de tamanho, acompanhando a
    expansão. Reforços na rolagem: medição relativa ao conteúdo do ScrollViewer
    (imune a transformações/recorte da janela), alvo limitado por
    `ExtentHeight − ViewportHeight`, e animação via `CompositionTarget.Rendering`
    (easeOutCubic manual) em vez de animar `VerticalOffset`.
13. **Auto-atualização (botão "update")** — novo `UpdateChecker` (Core) consulta
    `api.github.com/.../releases/latest` e compara a tag com
    `App.CurrentVersion` (comparação numérica por parte, não string). Havendo
    release novo, um **pill azul "update vX.Y.Z"** aparece no canto superior
    direito da área dos botões, flutuando ligeiramente acima do botão ⏭
    (`Margin 0,-33,2,0`), com glow azul (#5CB8FF, o mesmo da barra de progresso).
    Ao clicar: baixa `Translucid.zip` + `.sha256` do release para
    `%TEMP%\Translucid_Update_<guid>`, valida o checksum, extrai e gera um
    `Translucid_Update.cmd` (método KitLugia) que espera o PID do widget morrer,
    faz `xcopy /E /Y /Q /I` da pasta extraída para a pasta de instalação,
    relança o exe e apaga a temporária. O widget sai de verdade via
    `App.QuitForUpdate()` (bypassa o fechar-para-bandeja). Falha de download/
    extração restaura o pill para nova tentativa.
14. **Seek pelas letras** — clicar numa linha do painel pula a música para o
    instante dela no LRC. `MediaTracker.SeekAsync` valida
    `IsPlaybackPositionEnabled` e chama `TryChangePlaybackPositionAsync`
    (ticks); o clique recupera o tempo via `DataContext` da linha, reposiciona
    o relógio local (`_positionAtStamp`/`_positionStamp`) para a UI não contar
    do lugar antigo até o próximo evento SMTC e força `SyncLyrics()`. Se o app
    de origem negar o seek, o painel pisca em opacidade como feedback. Cursor
    vira mão sobre as linhas. Versão do app agora lida do assembly
    (`-p:Version`), não mais de constante hardcodada.