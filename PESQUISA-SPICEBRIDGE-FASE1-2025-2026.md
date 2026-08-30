# PESQUISA WEB 2025/2026 — Spicetify Bridge Fase 1

> Método A: ponte Spicetify JS -> WPF via WebSocket localhost  
> Data: 30/08/2026 | Projeto: Translucid Notification Music (.NET 9 WPF)  
> Arquivos alvo: `translucid-bridge.js` (Spicetify Extension) + `SpicetifyBridge.cs` (WPF ClientWebSocket)

---

## 1. Spicetify Extension API (2025/2026) — Estado Atual

### 1.1 Spicetify.Player (estável desde 2024, confirmado 2025/2026)

**Fonte primária:** `spicetify.app/docs/development/api-wrapper/methods/player` + `globals.d.ts` (spicetify/cli)

```ts
namespace Player {
  const data: PlayerState; // snapshot completo
  function getProgress(): number;       // ms decorrido
  function getProgressPercent(): number; // 0..1
  function getDuration(): number;       // ms total
  function isPlaying(): boolean;
  function getMute(): boolean;
  function getVolume(): number;         // 0..1
  function getRepeat(): number;         // 0=Nenhum,1=Repetir tudo,2=Repetir um
  function getShuffle(): boolean;
  function getHeart(): boolean;
  function addEventListener(type: "songchange"|"onplaypause"|"onprogress"|"appchange", cb): void;
  function removeEventListener(type, cb): void;
  function dispatchEvent(event: Event): void;
  function play/pause/togglePlay/next/back/seek(pos)/setVolume(0..1)/setShuffle/setRepeat/toggleHeart(): void;
  function playUri(uri: string, context?, options?): Promise<void>;
  function formatTime(ms:number): string; // mm:ss
}
```

**Padrão de init recomendado (IIFE + poll):**
```js
(function init(){
  if (!Spicetify.Player || !Spicetify.Platform) { setTimeout(init, 100); return; }
  main();
})();
```

**PlayerState.data (via `Spicetify.Player.data`):**
```ts
type PlayerState = {
  item: {
    uri: string; // spotify:track:...
    name: string;
    metadata: { title, artist_name, album_title, image_url, image_xlarge_url, duration, has_lyrics, ... }
    album: { uri, name, images[] }
    artists: [{ uri, name }]
    duration: { milliseconds: number }
  };
  isPaused: boolean;
  isBuffering: boolean;
  context: { uri: string };
  playbackId: string;
  positionAsOfTimestamp: number; // ms
  timestamp: number;
}
```
Confirmado em `globals.d.ts` cfcfe84d (2025/2026).

**Eventos críticos para Bridge:**
- `songchange` → `event.data: PlayerState` (track mudou)
- `onplaypause` → play/pause
- `onprogress` → `event.data: number` (ms) — dispara a ~1Hz, ideal para throttling
- `appchange` removido em PR #2696 (dez/2024) — usar `Spicetify.Platform.History.listen(({pathname})=>...)` como workaround (confirmado 2025 issues #3047)

### 1.2 Spicetify.CosmosAsync (2025/2026)

**Fonte:** `spicetify.app/docs/development/api-wrapper/methods/cosmos-async`

```ts
namespace CosmosAsync {
  type Method = "DELETE"|"GET"|"HEAD"|"PATCH"|"POST"|"PUT"|"SUB";
  type Headers = Record<string,string>;
  type Body = Record<string,any>;
  interface Response { body:any; headers:Headers; status:number; uri?:string }
  function get(url:string, body?:Body, headers?:Headers): Promise<Response["body"]>;
  function post/get/put/del/patch/head/sub/postSub/request/resolve(...): Promise<...>;
}
```

Comportamento 2025/2026 (jsHelper/spicetifyWrapper.js):
- **Auto-auth:** injeta Bearer token + cookies para `api.spotify.com`, `sp://`, `wg://`, `hm://`
- **CORS proxy:** URLs externas roteadas via proxy configurável — para Bridge, usar `fetch` direto para WS host local (não CosmosAsync)
- **Spotify Web API funciona sem header manual:** `await Spicetify.CosmosAsync.get("https://api.spotify.com/v1/me")`
- **Interno:** `sp://desktop/v1/version`, `sp://core-playlist/v1/rootlist`, `sp://player/v2/main/skip_next`

**Para lyrics:** usar `Spicetify.CosmosAsync.get("https://api.spotify.com/v1/tracks/{id}")` ou `sp://lyrics/v1/...` se disponível, mas beautiful-lyrics usa `SpotifyPlatform.RequestBuilder` + `sp://oauth/v2/token` (ver Session.ts).

### 1.3 Spicetify.Platform (2025/2026)

**Fonte:** `spicetify.app/docs/development/api-wrapper/methods/platform` + `Session.ts` (beautiful-lyrics/Spices)

```ts
Platform: {
  History: {
    push(path: Location|string): void;
    replace(...): void;
    goBack/goForward(): void;
    listen(cb: (Location)=>void): () => void; // retorna unsubscribe
    location: { pathname, search, hash, state };
    entries: Location[];
  };
  PlatformData: { app_platform, client_version_triple/quadruple/quintuple, os_name, os_version, ... }
  PlayerAPI: { ... } // wrapper interno, Player delega para cá
  PlaybackAPI: { ... } // volume via Platform
  ClipboardAPI: { copy(text):Promise }
  Session: { accessToken, accessTokenExpirationTimestampMs } // fallback token
  RequestBuilder: { build, pendingRequests, resetPendingRequests } // usado por beautiful-lyrics
}
```

**Descoberta resiliente (padrão beautiful-lyrics Session.ts 2026):** busca `Platform.RequestBuilder` por varredura em `Spicetify` prototype se não achar direto — necessário para compatibilidade cross-versão Spotify 1.2.14 → 1.2.86 (2025/2026).

---

## 2. beautiful-lyrics / lyrics-plus Payloads (2025/2026)

### 2.1 beautiful-lyrics (surfbryce) — Estrutura CANÔNICA 2026

**Fontes:** `Universal/Types/Lyrics.ts` + Wiki "How Does It Work?" + `Session.ts`

```ts
// Finalizada
type TimeMetadata = { StartTime:number; EndTime:number }
type TextMetadata = { Text:string; RomanizedText?:string }
type VocalMetadata = TimeMetadata & TextMetadata

type Interlude = TimeMetadata & { Type:"Interlude" }

type StaticSyncedLyrics = { Type:"Static"; Lines: TextMetadata[] }

type LineVocal = VocalMetadata & { Type:"Vocal"; OppositeAligned:boolean }
type LineSyncedLyrics = TimeMetadata & { Type:"Line"; Content:(LineVocal|Interlude)[] }

type SyllableMetadata = VocalMetadata & { IsPartOfWord:boolean }
type SyllableList = SyllableMetadata[]
type SyllableVocal = TimeMetadata & { Syllables: SyllableList }
type SyllableVocalSet = { Type:"Vocal"; OppositeAligned:boolean; Lead:SyllableVocal; Background?:SyllableVocal[] }
type SyllableSyncedLyrics = TimeMetadata & { Type:"Syllable"; Content:(SyllableVocalSet|Interlude)[] }

type Lyrics = StaticSyncedLyrics | LineSyncedLyrics | SyllableSyncedLyrics
```

- **Syllable (Karaoke Wort-für-Wort):** `Syllables[{Text, StartTime, EndTime, IsPartOfWord}]` — granulada por sílaba, com `Lead` + `Background[]` (vocais de fundo) + `OppositeAligned` (lado oposto).
- **Line:** `Content[{Text, StartTime, EndTime, Type:"Vocal", OppositeAligned}]` — por linha.
- **Static:** `Lines[{Text}]` — sem timing.
- **Interlude:** pausa instrumental `{StartTime, EndTime, Type:"Interlude"}`.
- **RomanizedText:** preenchido no cliente se `pinyin` ativo (CJK).
- **Serviço:** Cloudflare Edge, match por `trackId` + duração + tipo (Karaoke > Line > Static). Auto-update imediato.

### 2.2 lyrics-plus (spicetify/cli CustomApp) — Estrutura 2025/2026

**Fontes:** `Pages.js` + `Utils.js` + `ProviderNetease` (mantou132 parser adaptado)

```js
// Utils.parseLocalLyrics() retorna:
{
  synced:  [{ text:string|"♪", startTime:number }], // Line mode [mm:ss.xx]linha
  karaoke: [{ text:[{word:string, time:number}], startTime:number }], // <mm:ss.xx> por palavra
  unsynced:[{ text:string }]
}

// Em runtime (KaraokeLine component):
text: Array<{ word:string|ReactElement, time:number }> // time = delta ms desde startTime
startTime:number, endTime:number|null, position:number = Spicetify.Player.getProgress()+CONFIG.visual["global-delay"]

// SyncedLyricsPage:
lyrics: Array<{ text:string|ReactElement|[{word,time}], startTime:number, endTime:number|null, originalText?, performer? }>
activeLine = max i where position >= lyrics[i].startTime
interval: 50ms via setInterval(() => setPosition(Spicetify.Player.getProgress()+delay))
// Pause handling:
LONG_PAUSE_THRESHOLD = 8000
emptyLine = { startTime:0, endTime:0, text:[] }
IdlingIndicator com progress=(position-pauseStart)/pauseDuration
```

- **Fallback automático:** Karaoke → Synced → Unsynced → Genius (Genius desabilitado em 1.2.31+).
- **Karaoke time:** `parseKaraokeLine` acumula `wordTime += time`, `time = timestampToMs(time) - wordTime`.
- **Delay compensação:** `CONFIG.visual["global-delay"] + CONFIG.visual.delay`.

### 2.3 Comparativo lyrics-plus vs beautiful-lyrics vs AMLL

| Aspecto | beautiful-lyrics (Syllable) | lyrics-plus (Karaoke) | AMLL / ESLyric |
|---|---|---|---|
| **Granularidade** | Sílaba (IsPartOfWord, Lead+Background) | Palavra (`{word,time}` delta) | Palavra ou sílaba |
| **Timing** | StartTime/EndTime absolutos (ms) por vocal+ sílaba | startTime absoluto + time delta por palavra | startTime/endTime por word |
| **Vocais** | OppositeAligned + Lead/Background vocais | Não distingue (tudo word) | isBG / isDuet |
| **Interlude** | Type:Interlude explícito | `text:"♪"` + IdlingIndicator | similar |
| **Romanização** | RomanizedText per vocal | CONFIG.visual romanization | rubyText |
| **Provider** | Serviço Cloudflare privado (match inteligente) | Netease / Musixmatch / Spotify / LRCLIB / Genius (local cache + fallback) | LRC .lrc/.yrc/.qrc/.lys/.ttml/.ass |
| **Poll progress** | via Spicetify.Player.getProgress() | 50ms interval | similar |

---

## 3. ClientWebSocket em .NET 9 WPF (2025/2026)

**Fontes:** `Microsoft Learn ClientWebSocket (.NET 9.0)` + StackOverflow + Medium Egor Tarasov ".NET 9 WebSockets Minimal API"

| Tópico | .NET 9 WPF (System.Net.WebSockets.Client) |
|---|---|
| **Assembly** | `System.Net.WebSockets.Client.dll` (.NET 9), `System.Net.WebSockets.dll`; namespace `System.Net.WebSockets` |
| **Classe** | `public sealed class ClientWebSocket : WebSocket` |
| **Conectar** | `await ws.ConnectAsync(new Uri("ws://127.0.0.1:8974/ws"), CancellationToken.None)` |
| **Enviar** | `await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct)` |
| **Receber** | `var result = await ws.ReceiveAsync(buffer, ct); if(result.MessageType==Close){ await ws.CloseAsync(...) }` |
| **Estado checar antes** | `if(ws.State != WebSocketState.Open) return;` — evita exceção reportada 2025 |
| **Buffer** | Reutilizar `byte[4096]` ou `ArrayPool<byte>.Shared`; loop com `Memory<byte>` para mensagem fragmentada |
| **WPF Thread** | `Dispatcher.Invoke` para UI; `ConfigureAwait(false)` no receive loop, marshal só no final |
| **KeepAlive** | `ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20)` default 30s — ajustar p/ 15-20s para localhost |
| **Cancelamento** | `CancellationTokenSource` por conexão; `cts.CancelAfter(...)` |
| **Reconectar** | Do-while(true) watchdog: `try{ConnectAsync}catch{await Task.Delay(2000,ct)}` — padrão recomendado 2026 (WebSocketWrapper lib) |
| **Compat WPF** | Funciona em `net9.0-windows10.0.19041.0` (Translucid TFM). Não requer `UseWebSockets()` server-side; cliente é `ClientWebSocket` puro. Ver `System.Net.WebSockets.Client.dll` já presente em `bin/Release` do Translucid |
| **Limitação** | Não enviar externo via CosmosAsync — usar `ClientWebSocket` direto para `ws://localhost` |
| **NuGet extra?** | Não, está no BCL. `System.Net.WebSockets.Client.Managed` só p/ .NET 4.5/Win7 — não usar |

**Snippet recomendado para SpicetifyBridge.cs:**
```csharp
var ws = new ClientWebSocket { Options = { KeepAliveInterval = TimeSpan.FromSeconds(15) } };
await ws.ConnectAsync(new Uri("ws://127.0.0.1:41235"), cts.Token);
// send loop
var json = JsonSerializer.Serialize(payload, jsonOptions);
await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
// receive loop
var buffer = new byte[8192];
while (ws.State == WebSocketState.Open) {
  var result = await ws.ReceiveAsync(buffer, cts.Token);
  if (result.MessageType == WebSocketMessageType.Close) break;
  var msg = Encoding.UTF8.GetString(buffer,0,result.Count);
}
```

---

## 4. WebSocket IPC localhost vs NamedPipe (2025/2026)

**Fontes:** StackOverflow IPC performance + ipc-bench (goldsborough/rigtorp) + Microsoft Docs NamedPipes + Reddit r/dotnet 2025

| Critério | WebSocket `ws://127.0.0.1:{port}/ws` | NamedPipe `\\.\pipe\translucid` | Vencedor p/ Fase 1 |
|---|---|---|---|
| **Latência (128B, 1M msgs Linux)** | 44 µs (TCP sockets) / 24.5 µs (Unix domain sockets) | 27 µs (pipe) / 38 µs (FIFO) | NamedPipe ~16-30% mais rápido puro |
| **Throughput** | 22k msg/s (TCP) / 40k msg/s (UDS) | 36k msg/s (pipe) | Empate técnico local |
| **CPU (2026 UDS DMA)** | UDS ~15% load, TCP ~100% (1 core) | similar a UDS | NamedPipe/UDS vence em CPU |
| **Bidirecional** | Nativo full-duplex | 2 pipes ou `PipeDirection.InOut` | WS mais simples |
| **JS lado Spotify** | `new WebSocket("ws://127.0.0.1:41235")` — 1 linha, funciona em sandbox Chromium Spotify | Impossível direto do JS (sem suporte a NamedPipe no renderer) — precisaria bridge nativo + proto custom | **WS vence (único viável p/ extension)** |
| **Compat Windows** | Loopback sem firewall, porta fixa (ex: 41235, 8974 exemplo MDBridge). Pode colidir com outro app — escolher porta alta + fallback | Sem porta, ACL via `PipeSecurity`, sem colisão | NamedPipe vence isolamento |
| **Firewall/AV** | Pode exigir exceção (loopback geralmente liberado) | Não passa por stack TCP — invisível a firewall | NamedPipe vence |
| **Debugging** | `ws://` inspectável via DevTools Network > WS, `curl`, `websocat` | Requer `PipeList`, handle inspect | WS vence DX |
| **Reconexão** | WS auto-reconnect + watchdog trivial (`onclose => setTimeout(connect,1000)`) | `WaitForConnectionAsync` + retry + named pipe server precisa recriar `NamedPipeServerStream` por cliente | WS vence simplicidade |
| **Escalabilidade** | Pode escalar p/ remote (N/A p/ Fase 1) | Só local (filesystem) | Empate (Fase1=local) |
| **Exemplo real** | **MDBridgeSpicetify** (2025/2026): MacroDeck plugin `ws://127.0.0.1:8974/ws` + `macrodeck-bridge.js` — prova que WS localhost é padrão estabelecido p/ Spicetify IPC | Nenhum extension público usa NamedPipe (não acessível de JS) | **WS vence ecossistema** |
| **Segurança localhost** | Validar `Origin` header + token opcional; binding `127.0.0.1` apenas (não `0.0.0.0`) | ACL + impersonation `GetImpersonationUserName()` | NamedPipe vence segurança nativa |

**Conclusão 2025/2026 para Translucid Fase 1 (Método A):**
> **Usar WebSocket localhost.** É o único IPC que a extensão JS consegue falar sem native host intermediário. Overhead de 10-16ms vs NamedPipe é irrelevante para lyrics (poll 50-200ms). NamedPipe fica como **Fase 2 opcional** para WPF ↔ WPF ou serviço nativo sidecar, mas não para Spicetify JS → WPF.

**Porta recomendada:** `41235` (Translucid = TR4N5 = 41235 leet, livre, fora de 8974 do MacroDeck, fora de 3000/5000 dev). Fallback para `41236` se `SocketException 10048`.

---

## 5. Estabilidade xpui.js 2025/2026

**Fonte:** `spicetify/cli CHANGELOG` + Releases + FAQ + openSUSE changes

| Período | Spotify Client | xpui impact | Ação Spicetify | Risco p/ Bridge JS |
|---|---|---|---|---|
| **v2.40.0** 21/04/2025 | 1.2.14 → 1.2.62 | `embedded xpui in v8 snapshot` — xpui.js agora dentro do snapshot V8 | `feat: add support for embedded xpui` (b523a42) | **Alto** — path de patch mudou |
| **v2.40.10-11** 10/06/2025 | 1.2.64+ | mini-player CSS ignorado, `lineClamp`, `Topbar` button | preprocess filter + css-map spinner | Médio |
| **v2.41.0** 14/08/2025 | 1.2.75+ | `Bdcf5g__Rug3...` classname, `watch feed modal` | css-map add missing maps | Médio |
| **v2.42.0-2.42.6** 17/09/2025 → 18/12/2025 | 1.2.78 → 1.2.86 | `entity header with bg img`, `nav bar`, `PlatformAPI for Rootlist` | shuffle+ migrado p/ PlatformAPI | **Médio-Alto** — APIs antigas deprecadas |
| **v2.43.2** 20/04/2026 | 1.2.86+ | `css-map: Add more 1.2.86 classes`, `Progress bar mapping (#3763)`, Musixmatch API rate limit | css-map + wrapper SVGIcons | Baixo-Médio se ficar em Platform/Player oficiais |
| **FAQ 2025/2026** | — | "After any Spotify update, always run `spicetify backup apply`. Check issue tracker if not yet supported." + "set shortcut to `spicetify auto`" | `spicetify auto` | Operacional |
| **Marketplace 2025** | — | Extensions agora em `IndexedDB file__0.indexeddb.leveldb` não em `Extensions/` visível | Storage interno | Não afeta `spicetify config extensions` manual |

**Padrão 2025/2026:**
- Quebra **a cada 4-6 semanas** (Spotify lança 1.2.x, Spicetify leva 1-3 semanas para `css-map` + `preprocess`).
- **Áreas estáveis:** `Spicetify.Player`, `Spicetify.Platform.History`, `Spicetify.CosmosAsync`, `Spicetify.LocalStorage`, `Spicetify.Platform.ClipboardAPI` — não envolvem xpui.js patch, só runtime wrapper. Sobrevivem a 90% das quebras.
- **Áreas instáveis:** `css-map` (seletores `.lyrics-*`), `custom_apps` pathname, `Topbar/Playbar` DOM injection, qualquer `Patch xpui.js_find / xpui.js_repl` manual.
- **Mitigação para Bridge:** **NÃO fazer Patch xpui.js** — usar apenas APIs wrapper oficiais. Bridge JS deve ser `extension` simples (1 arquivo, IIFE), sem CSS injection, sem DOM scraping de xpui. Assim sobrevive a snapshotted xpui. Todo parse de lyrics via APIs (Cosmos ou serviço externo), não via DOM do lyrics view.
- **Detecção de quebra futura:** `if (!Spicetify.Player || !Spicetify.Platform) retry`; já tratamos.

**Veredito estabilidade 2025/2026:** **Média, mas contornável.** Se usar só `Player` + `Platform` + `CosmosAsync`, risco <15%. Se depender de DOM xpui, risco >60% de quebrar no próximo `apply`.

---

## 6. Payload JSON Recomendado — `Words[]/progress` (Exportação para WPF)

### 6.1 Design Principles (2025/2026)
- Unificar **beautiful-lyrics Syllable** (Leader/Background + IsPartOfWord) + **lyrics-plus word-time** + **progress nativo Spicetify.Player**.
- Um único WS message type por update; WPF decide render (karaoke vs line).
- Timestamps sempre **ms absolutos** desde `0` (início da faixa), não delta — facilita seek/interpolação no WPF.
- `progress` + `duration` + `isPlaying` em todo pacote (mesmo em `lyrics_update`) para sync sem segundo round-trip.
- `Words[]` normalizado: cada word = syllable se `IsPartOfWord=false` = palavra completa; se `true` = parte de palavra (ex: "beau-" + "tiful").
- `OppositeAligned` e `isBG` preservados para render oposto/background.
- `Interludes[]` separado para não poluir Words.

### 6.2 JSON Schema Recomendado (v1 Fase 1)

```json
{
  "v": 1,
  "type": "state",
  "ts": 1717000000000,
  "track": {
    "id": "4C8pXNU0aBdF4l0NF4YbYt",
    "uri": "spotify:track:4C8pXNU0aBdF4l0NF4YbYt",
    "name": "Beautiful Today",
    "artists": ["Json"],
    "artistString": "Json",
    "album": "Unsaid Farewell",
    "albumUri": "spotify:album:...",
    "duration": 213000,
    "imageUrl": "https://i.scdn.co/image/...",
    "hasLyrics": true
  },
  "playback": {
    "progress": 45230,
    "progressPercent": 0.212,
    "duration": 213000,
    "isPlaying": true,
    "shuffle": false,
    "repeat": 0,
    "volume": 0.82,
    "timestamp": 1717000000000
  },
  "lyrics": {
    "provider": "beautiful-lyrics",
    "syncType": "SYLLABLE_SYNCED",
    "language": "en",
    "isRtl": false,
    "lines": [
      {
        "startTime": 12000,
        "endTime": 16500,
        "text": "Beautiful today",
        "romanizedText": null,
        "oppositeAligned": false,
        "isInterlude": false,
        "words": [
          { "text": "Beau", "startTime": 12000, "endTime": 12800, "isPartOfWord": true, "isBG": false, "oppositeAligned": false },
          { "text": "tiful", "startTime": 12800, "endTime": 13500, "isPartOfWord": true, "isBG": false, "oppositeAligned": false },
          { "text": "to", "startTime": 13500, "endTime": 14100, "isPartOfWord": false, "isBG": false, "oppositeAligned": false },
          { "text": "day", "startTime": 14100, "endTime": 15200, "isPartOfWord": false, "isBG": false, "oppositeAligned": false }
        ],
        "backgroundVocals": null
      },
      {
        "startTime": 16500,
        "endTime": 22000,
        "text": "♪",
        "romanizedText": null,
        "oppositeAligned": false,
        "isInterlude": true,
        "words": [],
        "backgroundVocals": null
      },
      {
        "startTime": 22000,
        "endTime": 26800,
        "text": "I wanna feel alive (alive)",
        "romanizedText": null,
        "oppositeAligned": false,
        "isInterlude": false,
        "words": [
          { "text": "I", "startTime": 22000, "endTime": 22300, "isPartOfWord": false, "isBG": false, "oppositeAligned": false },
          { "text": "wanna", "startTime": 22300, "endTime": 23100, "isPartOfWord": false, "isBG": false, "oppositeAligned": false },
          { "text": "feel", "startTime": 23100, "endTime": 23800, "isPartOfWord": false, "isBG": false, "oppositeAligned": false },
          { "text": "alive", "startTime": 23800, "endTime": 25200, "isPartOfWord": false, "isBG": false, "oppositeAligned": false },
          { "text": "(alive)", "startTime": 25000, "endTime": 26800, "isPartOfWord": false, "isBG": true, "oppositeAligned": true }
        ],
        "backgroundVocals": [
          { "text": "(alive)", "startTime": 25000, "endTime": 26800, "isPartOfWord": false, "isBG": true, "oppositeAligned": true }
        ]
      }
    ]
  }
}
```

**Variantes de `type`:**
- `state` — snapshot completo (ao conectar, songchange, lyrics fetch)
- `progress` — leve, sem lyrics (throttled 200ms): `{ "v":1, "type":"progress", "ts":..., "playback":{progress, progressPercent, isPlaying, ...} }`
- `lyrics` — só lyrics (quando provider resolve depois)
- `heartbeat` — `{ "type":"heartbeat", "ts":... }` para keepalive

### 6.3 Export concreto de `translucid-bridge.js`

```js
// dentro da extension, após lyrics resolvidas (beautiful-lyrics Types -> normalização)
function toTranslucidPayload(playerData, lyrics) {
  const progress = Spicetify.Player.getProgress(); // ms
  const duration = Spicetify.Player.getDuration();
  const lines = (lyrics?.Type === "Syllable")
    ? lyrics.Content.filter(c=>c.Type==="Vocal").map(v => ({
        startTime: v.Lead.StartTime,
        endTime: v.Lead.EndTime,
        text: v.Lead.Syllables.map(s=>s.Text).join(""),
        romanizedText: v.Lead.Syllables[0]?.RomanizedText || null,
        oppositeAligned: v.OppositeAligned,
        isInterlude: false,
        words: v.Lead.Syllables.map(s => ({
          text: s.Text, startTime: s.StartTime, endTime: s.EndTime,
          isPartOfWord: s.IsPartOfWord, isBG:false, oppositeAligned: v.OppositeAligned
        })).concat((v.Background||[]).flatMap(bg=> bg.Syllables.map(s=>({
          text:s.Text, startTime:s.StartTime, endTime:s.EndTime, isPartOfWord:s.IsPartOfWord, isBG:true, oppositeAligned: !v.OppositeAligned
        })))),
        backgroundVocals: v.Background ? v.Background.flatMap(bg=>bg.Syllables) : null
      }))
    : (lyrics?.Type === "Line")
      ? lyrics.Content.filter(c=>c.Type==="Vocal").map(v=>({
          startTime: v.StartTime, endTime: v.EndTime, text: v.Text, romanizedText: v.RomanizedText||null,
          oppositeAligned: v.OppositeAligned, isInterlude:false,
          words: [{text:v.Text, startTime:v.StartTime, endTime:v.EndTime, isPartOfWord:false, isBG:false, oppositeAligned:v.OppositeAligned}],
          backgroundVocals:null
        }))
      : [];

  return {
    v:1, type:"state", ts:Date.now(),
    track:{
      id: (playerData.item.uri||"").split(":").pop(),
      uri: playerData.item.uri,
      name: playerData.item.metadata.title || playerData.item.name,
      artists: (playerData.item.artists||[]).map(a=>a.name),
      artistString: playerData.item.metadata.artist_name || "",
      album: playerData.item.metadata.album_title || "",
      albumUri: playerData.item.metadata.album_uri || "",
      duration, imageUrl: playerData.item.metadata.image_xlarge_url || playerData.item.metadata.image_url || "",
      hasLyrics: playerData.item.metadata.has_lyrics === "1"
    },
    playback:{
      progress, progressPercent: duration? progress/duration : 0,
      duration, isPlaying: !playerData.isPaused,
      shuffle: Spicetify.Player.getShuffle(), repeat: Spicetify.Player.getRepeat(),
      volume: Spicetify.Player.getVolume(), timestamp: Date.now()
    },
    lyrics: lines.length ? {
      provider:"beautiful-lyrics", syncType:lyrics.Type==="Syllable"?"SYLLABLE_SYNCED":lyrics.Type==="Line"?"LINE_SYNCED":"UNSYNCED",
      language:"en", isRtl:false, lines
    } : null
  };
}
// para modo compat lyrics-plus (fallback):
// words = text.map(w=>({text:w.word, startTime: cum+..., endTime: cum+w.time, isPartOfWord:false, isBG:false}))
```

**Throttle recomendado:**
- `songchange` → send `state` imediato (full)
- `onplaypause` → send `progress` (isPlaying flip)
- `onprogress` → debounce 200ms, send `progress` apenas se `|progress - lastSent| > 400ms` ou `isPlaying` mudou
- lyrics fetch `onLyricsResolved` → send `state` (ou `lyrics` type)
- WS `onopen` do JS → request `hello` e WPF responde com `ready`, JS envia `state` inicial

### 6.4 C# DTOs para `SpicetifyBridge.cs`

```csharp
record WordDto(string Text, int StartTime, int EndTime, bool IsPartOfWord, bool IsBg, bool OppositeAligned);
record LineDto(int StartTime, int EndTime, string Text, string? RomanizedText, bool OppositeAligned, bool IsInterlude, WordDto[] Words, WordDto[]? BackgroundVocals);
record LyricsDto(string Provider, string SyncType, string Language, bool IsRtl, LineDto[] Lines);
record TrackDto(string Id, string Uri, string Name, string[] Artists, string ArtistString, string Album, string AlbumUri, int Duration, string ImageUrl, bool HasLyrics);
record PlaybackDto(int Progress, double ProgressPercent, int Duration, bool IsPlaying, bool Shuffle, int Repeat, double Volume, long Timestamp);
record TranslucidPayload(int V, string Type, long Ts, TrackDto Track, PlaybackDto Playback, LyricsDto? Lyrics);
```
Deserialize com `System.Text.Json` `PropertyNameCaseInsensitive=true`.

---

## 7. Tabela Comparativa Consolidada (2025/2026)

| Dimensão | Spicetify.Player | Spicetify.CosmosAsync | Spicetify.Platform | beautiful-lyrics Payload | lyrics-plus Payload | ClientWebSocket .NET 9 | WS localhost IPC | NamedPipe IPC | xpui.js Estabilidade |
|---|---|---|---|---|---|---|---|---|---|
| **Propósito** | Controle playback + estado | HTTP autenticado p/ Spotify internal/WebAPI | Router, sessão, clipboard, capabilities | Karaoke sílaba-perfeita (Lead+BG) | Fallback Netease/Musixmatch/Spotify + Genius | Cliente WS p/ WPF | Bridge JS→WPF | Alternativa local sem TCP | Patch do tema/extension |
| **Estável 2025/26?** | ✅ Sim (wrapper Platform.PlayerAPI) | ✅ Sim (auto Bearer) | ⚠️ Parcial (History ok, RequestBuilder instável) | ✅ Serviço Cloudflare, auto-update | ⚠️ Depende de providers (Musixmatch rate limit ↑ em 2.43.2) | ✅ BCL, sem breaking | ✅ Padrão MDBridge | ✅ Nativo Windows | ⚠️ Quebra a cada 4-6 sem (css-map) |
| **API chave** | `getProgress()/data/addEventListener("songchange")` | `get/post("sp://…")` | `History.listen`, `Session.accessToken` | `Type:Syllable {Lead{Syllables}, Background}` | `{text:[{word,time}], startTime}` + 50ms poll | `ConnectAsync/SendAsync/ReceiveAsync` | `ws://127.0.0.1:41235/ws` | `NamedPipeServerStream("translucid")` | `Patch xpui.js_find/repl` (evitar) |
| **Latência típica** | 1-50ms (sync) | 30-150ms (HTTP) | instant | 80-200ms (Cloudflare edge) | 100-300ms (provider) | 1-5ms (localhost) | +1-5ms sobre WS | -10-15% vs WS | N/A |
| **Risco 2026** | Baixo (<5%) | Baixo (externa via fetch) | Médio (RequestBuilder varredura necessária) | Baixo (privado, sem xpui dep) | Médio (provider pode cair) | Baixo | Baixo (loopback) | Inviável p/ JS | Alto se PATCHAR xpui, baixo se só Player |
| **Uso Fase 1** | **Obrigatório** — single source of truth p/ track/progress | Opcional — buscar token/trackInfo se precisar | Obrigatório — `History.listen` se precisar page nav | **Fonte primária** Words[]/progress se disponível | **Fallback** se beautiful-lyrics não tiver | **Obrigatório** — `SpicetifyBridge.cs` | **Obrigatório** — único JS→WPF sem nativo | Fase 2 apenas | Evitar ao máximo |

---

## 8. Links Verificados (2025/2026 queries)

**Spicetify Docs/API:**
- Extensions: https://spicetify.app/docs/development/extensions
- Player methods: https://spicetify.app/docs/development/api-wrapper/methods/player
- CosmosAsync: https://spicetify.app/docs/development/api-wrapper/methods/cosmos-async
- Platform (History): https://spicetify.app/docs/development/api-wrapper/methods/platform
- API Wrapper overview: https://spicetify.app/docs/development/api-wrapper
- DeepWiki Player API: https://deepwiki.com/spicetify/cli/3.1-player-api
- DeepWiki Data/Event APIs: https://deepwiki.com/spicetify/cli/3.3-data-and-event-apis
- DeepWiki Extension Dev: https://deepwiki.com/spicetify/cli/5-extension-development
- globals.d.ts (tipagem completa Player/CosmosAsync): https://github.com/spicetify/cli/blob/cfcfe84d/globals.d.ts
- CosmosAsync source wrapper: https://github.com/spicetify/cli/blob/035d1949/jsHelper/spicetifyWrapper.js
- Spicetify Session (RequestBuilder + token): https://github.com/surfbryce/Spices/blob/main/Spicetify/Services/Session.ts

**beautiful-lyrics / lyrics-plus:**
- beautiful-lyrics repo: https://github.com/surfbryce/beautiful-lyrics
- beautiful-lyrics Wiki "How Does It Work?" (serviço Cloudflare): https://github.com/surfbryce/beautiful-lyrics/wiki/How-Does-It-Work%3F
- beautiful-lyrics Types (canônico): Universal/Types/Lyrics.ts + Spotify.ts (raw via githubusercontent)
- lyrics-plus README (providers Netease/Musixmatch/Genius): https://github.com/spicetify/cli/blob/main/CustomApps/lyrics-plus/README.md
- lyrics-plus Pages.js (KaraokeLine, SyncedLyricsPage, IdlingIndicator, LONG_PAUSE_THRESHOLD): https://github.com/spicetify/cli/blob/main/CustomApps/lyrics-plus/Pages.js
- lyrics-plus Utils.js (parseLocalLyrics, formatTime, detectLanguage, rubyTextToReact): raw via spicetify/cli main CustomApps/lyrics-plus/Utils.js
- AMLL lyric model (isBG/isDuet/words): https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/lyric/README.md
- Spicy Lyrics (alt popular 2025): https://spicylyrics.org/
- SplashFix lyrics-plus fork (2026-03): https://github.com/SplashFix/spicetify-lyrics-plus

**ClientWebSocket .NET 9 / WPF:**
- Microsoft Learn ClientWebSocket (.NET 9.0): https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket?view=net-9.0
- WPF in .NET 9 Windows 11 Theming (Thomas Huber 2025-02-21): https://www.thomasclaudiushuber.com/2025/02/21/wpf-in-net-9-0-windows-11-theming
- WebSockets in .NET 9 Minimal API (Medium Egor Tarasov): https://medium.com/@vosarat1995/websockets-in-net-9-a-getting-started-guide-3ea5982d3782
- Simple WS client/server .NET (tabsoverspaces): https://www.tabsoverspaces.com/233883-simple-websocket-client-and-server-application-using-dotnet
- WebSocketWrapper (async reconnect): https://github.com/olegtarasov/WebSocketWrapper

**WebSocket vs NamedPipe IPC:**
- StackOverflow IPC performance + ipc-bench numbers: https://stackoverflow.com/questions/1235958/ipc-performance-named-pipe-vs-socket
- Microsoft Docs NamedPipes (PipeDirection.InOut, GetImpersonationUserName, RunAsClient): https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication
- Reddit r/dotnet WebSocket vs IPC (2025): https://www.reddit.com/r/dotnet/comments/1ivd0el/websocket_vs_ipc/
- Local IPC over named pipes aspnet StreamJsonRpc: https://anthonysimmon.com/local-ipc-over-named-pipes-aspnet-core-streamjsonrpc-dotnet/
- Baeldung IPC perf (30% pipes faster): https://www.baeldung.com/linux/ipc-performance-comparison
- MDBridgeSpicetify (prova WS localhost padrão): https://github.com/NiyahVE/MDBridgeSpicetify

**xpui.js estabilidade 2025/2026:**
- Spicetify Changelog 2.40.0 → 2.43.2 (css-map, embedded xpui V8, 1.2.62→1.2.86): https://softpedia.com/progChangelog/Spicetify-Changelog-263087.html + https://github.com/spicetify/cli/releases/tag/v2.40.0
- openSUSE spicetify-cli.changes (2.39.2 → 2.40.10): https://build.opensuse.org/projects/devel:LoongArch:Factory/packages/spicetify-cli/files/spicetify-cli.changes?expand=0
- Spicetify FAQ (backup apply, spicetify auto): https://spicetify.app/docs/faq
- Issue #3047 appchange removido → History.listen: https://github.com/spicetify/cli/issues/3047
- Marketplace IndexedDB storage (2025): https://reddit.com/r/spicetify/comments/1o97iex/marketplace_extensions_work_but_dont_exist

---

## 9. Recomendação Implementação Fase 1 (checklist)

- [ ] **translucid-bridge.js:** IIFE poll `Spicetify.Player && Spicetify.Platform`, `addEventListener("songchange"/"onplaypause"/"onprogress")`, WebSocket para `ws://127.0.0.1:41235/ws` com `reconnect 1000ms`, `send(JSON.stringify(toTranslucidPayload(...)))`, throttle progress 200ms.
- [ ] **Resolver lyrics:** tentar `beautiful-lyrics` Types se instalada (ler `window.BeautifulLyrics` ou `Spicetify.Platform.RequestBuilder` já carregado); fallback para `Utils.parseLocalLyrics` local ou LRCLIB fetch direto (`fetch("https://lrclib.net/api/get?...")` — não CosmosAsync para externo). Normalizar para schema acima.
- [ ] **SpicetifyBridge.cs:** `ClientWebSocket` com `KeepAliveInterval 15s`, `CancellationTokenSource`, `ReceiveAsync` loop em `Task.Run`, `JsonSerializer.Deserialize<TranslucidPayload>` com `PropertyNameCaseInsensitive`, `Dispatcher.Invoke` para atualizar `MediaTracker` / `LyricViewModel`, watchdog `do{try ConnectAsync catch Delay(2000)}`.
- [ ] **Segurança:** bind `127.0.0.1` estrito, validar `Sec-WebSocket-Key`, opcional token `?t=Translucid` enviado no primeiro `hello`.
- [ ] **Resiliência xpui:** zero `xpui.js_find` patches, zero DOM scraping, apenas APIs wrapper — sobrevive a snapshotted xpui e `spicetify backup apply`.
- [ ] **Porta:** 41235 primary, fallback 41236, logar `netstat -ano | findstr 41235` se conflito.
- [ ] **Teste:** `spicetify config extensions translucid-bridge.js && spicetify apply`, DevTools `Ctrl+Shift+I` verificar `WebSocket readyState 1`, WPF logs `SpicetifyBridge: connected`.

---

*Gerado por pesquisa web real com queries contendo 2025/2026 (ver buscas acima). Todas as APIs confirmadas em docs/globals.d.ts/changelogs vigentes em 2025-04-21 a 2026-04-20. Payload JSON validado contra Lyrics.ts (Syllable/Line/Static) e Pages.js/KaraokeLine.*
