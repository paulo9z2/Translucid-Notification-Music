/**
 * translucid-bridge.js — Spicetify extension (Fase 1 método A)
 * Escuta o Spotify (via Spicetify) e envia letras/estado AO VIVO pro Translucid WPF
 * via WebSocket localhost:4389 (WPF é SERVIDOR, este JS é CLIENTE).
 *
 * Instalação:
 *   1) copie este arquivo para %appdata%\spicetify\Extensions\translucid-bridge.js
 *   2) spicetify config extensions translucid-bridge.js
 *   3) spicetify apply   (reaplique após cada update do Spotify)
 *   4) No Translucid: Configurações → Ponte Spicetify → ON (lembra em ui.json)
 *
 * Fase 1: zero DLL, zero AV flag, só JS + WebSocket. Fallback LRCLIB continua igual se off.
 * Inspirado em: Spicetify.Player/CosmosAsync docs, Beautiful Lyrics (surfbryce/beautiful-lyrics),
 * lyrics-plus (spicetify/cli/CustomApps/lyrics-plus), Burnt-Sushi/YSpotify (DLL real — aqui não usamos).
 */
(function TranslucidBridge() {
    const PORT = 4389;
    const URI = `ws://127.0.0.1:${PORT}/`;
    const RECONNECT_MIN = 1200;
    const RECONNECT_MAX = 15000;
    const POLL_MS = 600;
    const TAG = "[TranslucidBridge]";

    let ws = null;
    let reconnectDelay = RECONNECT_MIN;
    let pollTimer = null;
    let lastPayloadJson = "";
    let lastTrackId = "";

    function log(...a) { try { console.log(TAG, ...a); } catch {}
    }
    function warn(...a) { try { console.warn(TAG, ...a); } catch {} }

    // Espera Spicetify pronto (xpui.js pode demorar após boot)
    function ready(cb) {
        if (window.Spicetify && Spicetify.Player && Spicetify.Player.data) return cb();
        // Fallback Platform
        if (window.Spicetify && Spicetify.Platform) return cb();
        setTimeout(() => ready(cb), 350);
    }

    function getTrack() {
        try {
            const d = Spicetify.Player?.data || Spicetify.Platform?.PlayerAPI?._state;
            if (d?.item) {
                const it = d.item;
                return {
                    name: it.name || it.metadata?.title || "",
                    artist: (it.artists || []).map(a => a.name).join(", ") || it.metadata?.artist_name || "",
                    album: it.album?.name || it.metadata?.album_title || "",
                    uri: it.uri || it.metadata?.uri || "",
                    id: (it.uri || "").split(":").pop() || it.metadata?.uri?.split(":").pop() || "",
                };
            }
            // fallback Spicetify.Player.getTrack?
            if (Spicetify.Player?.getTrack) {
                const t = Spicetify.Player.getTrack();
                if (t) return { name: t.name || t.title || "", artist: t.artist || "", album: t.album || "", uri: t.uri || "", id: t.uri?.split(":").pop() || "" };
            }
        } catch (e) { warn("getTrack", e); }
        return null;
    }

    function getProgress() {
        try {
            if (Spicetify.Player?.getProgress) return Math.round(Spicetify.Player.getProgress());
            if (Spicetify.Player?.data?.progress) return Math.round(Spicetify.Player.data.progress);
            if (Spicetify.Platform?.PlayerAPI?.getState) {
                const s = Spicetify.Platform.PlayerAPI.getState();
                if (s && typeof s.positionAsOfTimestamp === "number") return Math.round(s.positionAsOfTimestamp);
            }
        } catch {}
        return 0;
    }
    function getDuration() {
        try {
            if (Spicetify.Player?.getDuration) return Math.round(Spicetify.Player.getDuration());
            if (Spicetify.Player?.data?.item?.duration?.milliseconds) return Spicetify.Player.data.item.duration.milliseconds;
            if (Spicetify.Player?.data?.duration) return Math.round(Spicetify.Player.data.duration);
        } catch {}
        return 0;
    }
    function isPlaying() {
        try {
            if (typeof Spicetify.Player?.isPlaying === "function") return !!Spicetify.Player.isPlaying();
            if (typeof Spicetify.Player?.data?.isPaused === "boolean") return !Spicetify.Player.data.isPaused;
            if (Spicetify.Platform?.PlayerAPI?._state?.isPaused !== undefined) return !Spicetify.Platform.PlayerAPI._state.isPaused;
        } catch {}
        return true;
    }

    // ---- Scraper de letras (Beautiful Lyrics > lyrics-plus > DOM genérico) ----
    function scrapeLyrics() {
        // 1) Beautiful Lyrics (surfbryce/beautiful-lyrics)
        // Tentativas por ordem de probabilidade — DOM real pode variar por versão
        const candidates = [
            // Beautiful Lyrics
            ".beautiful-lyrics .lyric-line",
            ".beautiful-lyrics [data-lyric]",
            "[data-testid='beautiful-lyrics'] .lyric",
            ".lyrics-beautiful .line",
            // lyrics-plus
            "#lyrics-plus-container .lyric",
            ".lyrics-plus .line",
            "[data-testid='lyrics-plus'] .lyric",
            // generic fallback
            "[data-lyric-id] .lyric-line",
            ".lyric-line",
            ".lyricLine",
        ];
        let nodes = [];
        for (const sel of candidates) {
            try {
                const found = document.querySelectorAll(sel);
                if (found && found.length >= 2) { nodes = Array.from(found); break; }
            } catch {}
        }
        if (nodes.length < 2) {
            // fallback profundo: procura qualquer container com >3 linhas de texto curto que parecem letra
            // Heurística: divs com 10-80 chars, dentro de main / xpui
            try {
                const root = document.querySelector("main") || document.body;
                const all = root.querySelectorAll("div, span, p");
                const maybe = [];
                for (const el of all) {
                    const t = (el.textContent || "").trim();
                    if (t.length < 10 || t.length > 90) continue;
                    if (el.children.length > 3) continue; // container, não linha
                    // parece letra se está dentro de área de lyrics
                    const r = el.getBoundingClientRect();
                    if (r.height < 12 || r.height > 48) continue;
                    maybe.push(el);
                    if (maybe.length > 30) break;
                }
                if (maybe.length >= 5) nodes = maybe.slice(0, 24);
            } catch {}
        }
        if (nodes.length < 2) return null;

        const lines = [];
        for (let i = 0; i < nodes.length; i++) {
            const el = nodes[i];
            let text = (el.textContent || "").trim().replace(/\s+/g, " ");
            if (!text || text.length < 2) continue;
            if (/^(letra|lyrics|provided by|musixmatch|spotify)/i.test(text)) continue;

            // timestamps: tenta data-* attributes
            let start = parseInt(el.getAttribute("data-start") || el.getAttribute("data-time") || el.getAttribute("data-starttime") || el.dataset?.start || "", 10);
            let end = parseInt(el.getAttribute("data-end") || el.getAttribute("data-endtime") || el.dataset?.end || "", 10);
            // Beautiful Lyrics pode usar style --progress ou classes, sem timestamp; usa index * 2500 como fallback
            if (!Number.isFinite(start)) start = i * 2600;
            if (!Number.isFinite(end) || end <= start) end = start + 2300;

            // words: tenta filhos .word / span
            let words = null;
            const wordEls = el.querySelectorAll(".word, .syllable, span[data-word], .beautiful-word");
            if (wordEls.length >= 2) {
                words = [];
                for (const w of wordEls) {
                    const wt = (w.textContent || "").trim();
                    if (!wt) continue;
                    let ws = parseInt(w.getAttribute("data-start") || w.dataset?.start || "", 10);
                    let we = parseInt(w.getAttribute("data-end") || w.dataset?.end || "", 10);
                    if (!Number.isFinite(ws)) ws = start + words.length * 220;
                    if (!Number.isFinite(we) || we <= ws) we = ws + 220;
                    words.push({ text: wt, startMs: ws, endMs: we });
                }
                // fallback se não tinha timestamp por palavra: distribui proporcional
                if (words.length >= 2 && words.every(q => q.startMs === start)) {
                    const dur = end - start;
                    const per = dur / words.length;
                    words.forEach((q, idx) => { q.startMs = start + idx * per; q.endMs = start + (idx + 1) * per; });
                }
            } else {
                // tenta LRC estendido dentro do texto: <mm:ss.xx>palavra
                const m = text.match(/<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>/);
                if (m) {
                    // deixa pro WPF: ele já limpa LRC tags; aqui só envia raw
                }
            }

            lines.push({ startMs: start, endMs: end, text, words });
        }
        if (lines.length < 2) return null;
        // ordena e normaliza 0-based
        lines.sort((a, b) => a.startMs - b.startMs);
        // Se start global não é 0, desloca para 0 se parecer relativo
        if (lines[0].startMs > 8000) {
            const off = lines[0].startMs;
            lines.forEach(l => { l.startMs -= off; l.endMs -= off; if (l.words) l.words.forEach(w => { w.startMs -= off; w.endMs -= off; }); });
        }
        return lines;
    }

    function buildPayload(track, lyrics) {
        const pos = getProgress();
        const dur = getDuration();
        const playing = isPlaying();
        // activeLine: quem tem start <= pos < end
        let activeLine = -1;
        if (lyrics && lyrics.length) {
            for (let i = 0; i < lyrics.length; i++) {
                if (pos >= lyrics[i].startMs && pos < lyrics[i].endMs) { activeLine = i; break; }
                if (lyrics[i].startMs <= pos && (i === lyrics.length - 1 || lyrics[i + 1].startMs > pos)) activeLine = i;
            }
        }
        return {
            type: "translucid-lyrics",
            track: track?.name || "",
            artist: track?.artist || "",
            album: track?.album || "",
            positionMs: pos,
            durationMs: dur,
            isPlaying: playing,
            lyrics: lyrics || [],
            activeLine,
        };
    }

    function send(payload) {
        if (!ws || ws.readyState !== WebSocket.OPEN) return false;
        try {
            const json = JSON.stringify(payload);
            // evita spam idêntico: só envia se mudou track, letra ou activeLine
            if (json === lastPayloadJson) return true;
            // para heartbeat de posição, permite reenviar a cada 3 polls mesmo igual
            const isHeartbeat = payload.lyrics.length === 0;
            if (!isHeartbeat) lastPayloadJson = json;
            ws.send(json);
            return true;
        } catch (e) { warn("send", e); return false; }
    }

    function connect() {
        try { if (ws) { try { ws.close(); } catch {} } } catch {}
        ws = null;
        log(`conectando em ${URI}…`);
        try {
            ws = new WebSocket(URI);
        } catch (e) { warn("WebSocket ctor", e); scheduleReconnect(); return; }

        ws.onopen = () => {
            log("conectado ●");
            reconnectDelay = RECONNECT_MIN;
            // hello
            try { ws.send(JSON.stringify({ type: "hello", src: "translucid-bridge.js", ver: "1.0-fase1" })); } catch {}
            startPoll();
        };
        ws.onclose = () => {
            log("desconectado, reagendando…");
            stopPoll();
            scheduleReconnect();
        };
        ws.onerror = (e) => {
            // onerror seguido de onclose de qualquer jeito
            warn("ws error", e?.message || e);
        };
        ws.onmessage = (ev) => {
            try {
                const msg = ev.data;
                if (msg === "pong" || (typeof msg === "string" && msg.includes("pong"))) return;
                if (msg === "ping") { try { ws.send("pong"); } catch {} return; }
            } catch {}
        };
    }

    function scheduleReconnect() {
        setTimeout(connect, reconnectDelay);
        reconnectDelay = Math.min(reconnectDelay * 1.6, RECONNECT_MAX);
    }

    function startPoll() {
        stopPoll();
        pollTimer = setInterval(() => {
            try {
                const track = getTrack();
                if (!track || !track.name) return;
                const id = track.id || track.uri || track.name + "|" + track.artist;
                const trackChanged = id !== lastTrackId;
                if (trackChanged) lastTrackId = id;

                const lyrics = scrapeLyrics(); // null se não achou (Beautiful Lyrics fechado)
                // Se não há letras visíveis, manda heartbeat só com posição (WPF usa LRCLIB fallback)
                // Mas se mudou de faixa, força payload mesmo sem lyrics pra WPF invalidar cache
                const payload = buildPayload(track, lyrics || []);
                // evita enviar vazio sem mudança
                const hasLyrics = payload.lyrics && payload.lyrics.length >= 2;
                if (!hasLyrics && !trackChanged) {
                    // heartbeat leve a cada 3s só pra sync de posição
                    if (Date.now() % 3000 < POLL_MS) send(payload);
                    return;
                }
                send(payload);
            } catch (e) { warn("poll", e); }
        }, POLL_MS);
    }
    function stopPoll() { if (pollTimer) { clearInterval(pollTimer); pollTimer = null; } }

    // boot
    ready(() => {
        log("Spicetify pronto, iniciando ponte…");
        connect();
        // reconecta quando Spotify resume de sleep
        try {
            document.addEventListener("visibilitychange", () => {
                if (document.visibilityState === "visible" && (!ws || ws.readyState !== WebSocket.OPEN)) connect();
            });
        } catch {}
        // expõe no console para debug: Spicetify.TranslucidBridge
        try { Spicetify.TranslucidBridge = { connect, getTrack, scrapeLyrics, ws: () => ws }; } catch {}
    });
})();
