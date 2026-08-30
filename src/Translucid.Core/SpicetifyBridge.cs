using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Translucid.Core;

/// <summary>
/// Payload que a extension Spicetify (translucid-bridge.js) envia via WebSocket localhost.
/// Mapeia direto para LyricLine/WordSpan do Translucid — zero conversão ad-hoc no JS.
/// </summary>
public sealed class SpicetifyLyricPayload
{
    public string Type { get; set; } = "translucid-lyrics";
    public string Track { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int PositionMs { get; set; }
    public int DurationMs { get; set; }
    public bool IsPlaying { get; set; } = true;
    public List<SpicetifyLyricLine> Lyrics { get; set; } = new();
    public int ActiveLine { get; set; } = -1;
    public double? Progress { get; set; }
}

public sealed class SpicetifyLyricLine
{
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public string Text { get; set; } = "";
    public List<SpicetifyWord>? Words { get; set; }
}

public sealed class SpicetifyWord
{
    public string Text { get; set; } = "";
    public int StartMs { get; set; }
    public int EndMs { get; set; }
}

/// <summary>
/// Servidor WebSocket localhost que recebe o payload da extension Spicetify.
/// WPF é SERVIDOR (TcpListener 127.0.0.1:port + handshake RFC6455), a extension JS é CLIENTE (new WebSocket).
/// Por que servidor? JS em CEF não pode escutar porta — só o .NET pode.
/// Bypass de HttpListener (sem precisar netsh urlacl) via TcpListener puro — porta 4389 localhost livre.
/// 100% passivo: não injeta no Spotify.exe, só escuta. Reconecta automático, modo “dormindo” quando off.
/// </summary>
public sealed class SpicetifyBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly object _gate = new();
    private bool _disposed;

    public bool IsConnected
    {
        get { lock (_gate) return _client is not null && _stream is not null && _client.Connected; }
    }
    public bool IsEnabled => AppSettings.Current.SpicetifyBridgeEnabled;

    public event Action<LyricLine[], SpicetifyLyricPayload>? LyricsReceived;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? StatusChanged;

    private int Port => AppSettings.Current.SpicetifyBridgePort;
    private string WsUriDisplay => $"ws://127.0.0.1:{Port}/";

    public void StartIfEnabled()
    {
        if (!AppSettings.Current.SpicetifyBridgeEnabled) return;
        Start();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Debug.WriteLine($"[SpicetifyBridge] servidor em {WsUriDisplay}");
            StatusChanged?.Invoke($"ponte Spicetify ouvindo em {Port}…");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        TcpListener? listener;
        TcpClient? client;
        NetworkStream? stream;
        lock (_gate)
        {
            cts = _cts; task = _acceptTask; listener = _listener; client = _client; stream = _stream;
            _cts = null; _acceptTask = null; _listener = null; _client = null; _stream = null;
        }
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { stream?.Close(); } catch { }
        try { client?.Close(); } catch { }
        try { if (task is not null) task.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { cts?.Dispose(); } catch { }
        ConnectionChanged?.Invoke(false);
        StatusChanged?.Invoke("ponte Spicetify parada");
        Debug.WriteLine("[SpicetifyBridge] parado");
    }

    public void RestartIfPortChanged()
    {
        Stop();
        if (AppSettings.Current.SpicetifyBridgeEnabled) Start();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!AppSettings.Current.SpicetifyBridgeEnabled)
            {
                StatusChanged?.Invoke("ponte Spicetify dormindo (off)");
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            TcpListener listener = new(IPAddress.Loopback, Port);
            lock (_gate) _listener = listener;
            try
            {
                listener.Start();
                StatusChanged?.Invoke($"aguardando Spicetify em {WsUriDisplay}…");
                Debug.WriteLine($"[SpicetifyBridge] TcpListener start :{Port}");

                while (!ct.IsCancellationRequested && AppSettings.Current.SpicetifyBridgeEnabled)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted) { break; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SpicetifyBridge] Accept falhou: {ex.Message}");
                        try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }
                        continue;
                    }

                    // handshake RFC6455
                    NetworkStream stream = client.GetStream();
                    bool ok;
                    try
                    {
                        ok = await DoHandshakeAsync(client, stream, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SpicetifyBridge] handshake falhou: {ex.Message}");
                        try { client.Close(); } catch { }
                        continue;
                    }
                    if (!ok)
                    {
                        try { client.Close(); } catch { }
                        continue;
                    }

                    lock (_gate) { _client = client; _stream = stream; }
                    ConnectionChanged?.Invoke(true);
                    StatusChanged?.Invoke("Spicetify conectado ● — letra ao vivo");
                    Debug.WriteLine("[SpicetifyBridge] client conectado, handshake OK");

                    await ReceiveLoopAsync(client, stream, ct).ConfigureAwait(false);

                    lock (_gate) { if (_client == client) { _client = null; _stream = null; } }
                    ConnectionChanged?.Invoke(false);
                    StatusChanged?.Invoke("Spicetify desconectado — aguardando reconexão…");
                    try { client.Close(); } catch { }
                    // volta a aceitar novo cliente (Spotify reiniciado)
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Debug.WriteLine($"[SpicetifyBridge] AcceptLoop erro: {ex.Message}");
                StatusChanged?.Invoke("ponte Spicetify aguardando…");
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { return; }
            }
            finally
            {
                try { listener.Stop(); } catch { }
                lock (_gate) if (_listener == listener) _listener = null;
            }
        }
    }

    private static async Task<bool> DoHandshakeAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        // Lê até \r\n\r\n (header HTTP)
        var sb = new StringBuilder(2048);
        var buf = new byte[4096];
        client.ReceiveTimeout = 4000;
        int total = 0;
        while (total < 8192 && !sb.ToString().Contains("\r\n\r\n"))
        {
            int read;
            try { read = await stream.ReadAsync(buf, ct).ConfigureAwait(false); } catch { return false; }
            if (read <= 0) return false;
            sb.Append(Encoding.UTF8.GetString(buf, 0, read));
            total += read;
            if (total > 8192) break;
        }
        var req = sb.ToString();
        var match = Regex.Match(req, @"Sec-WebSocket-Key:\s*(.+)\r\n", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var key = match.Groups[1].Value.Trim();
        var accept = ComputeAccept(key);
        var resp = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        var respBytes = Encoding.UTF8.GetBytes(resp);
        await stream.WriteAsync(respBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static string ComputeAccept(string key)
    {
        var concat = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(concat));
        return Convert.ToBase64String(hash);
    }

    private async Task ReceiveLoopAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[14];
        while (!ct.IsCancellationRequested && client.Connected)
        {
            // Lê header (2 bytes mínimo)
            int r;
            try { r = await ReadExactAsync(stream, header, 0, 2, ct).ConfigureAwait(false); } catch { return; }
            if (r == 0) return;
            bool fin = (header[0] & 0x80) != 0;
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long payloadLen = header[1] & 0x7F;

            if (opcode == 0x8) { // close
                Debug.WriteLine("[SpicetifyBridge] close frame");
                return;
            }
            if (opcode == 0x9) { // ping → pong
                // consome payload e responde pong
                int extLen = 0;
                if (payloadLen == 126) extLen = 2;
                else if (payloadLen == 127) extLen = 8;
                if (extLen > 0) await ReadExactAsync(stream, header, 2, extLen, ct).ConfigureAwait(false);
                long len = payloadLen;
                if (len == 126) len = (header[2] << 8) | header[3];
                else if (len == 127) len = BitConverter.ToInt64(header.Skip(2).Take(8).Reverse().ToArray(), 0);
                byte[] mask = new byte[4];
                if (masked) await ReadExactAsync(stream, mask, 0, 4, ct).ConfigureAwait(false);
                var pingPayload = new byte[len];
                if (len > 0) await ReadExactAsync(stream, pingPayload, 0, (int)len, ct).ConfigureAwait(false);
                if (masked) for (int i = 0; i < len; i++) pingPayload[i] ^= mask[i % 4];
                // responde pong 0xA
                try { await SendFrameAsync(stream, pingPayload, 0xA, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }
            if (opcode != 0x1 && opcode != 0x2 && opcode != 0x0) // só text/binary/continuation
            {
                // ignora extensão
                return;
            }

            int headerSize = 2;
            if (payloadLen == 126) { await ReadExactAsync(stream, header, 2, 2, ct).ConfigureAwait(false); headerSize = 4; payloadLen = (header[2] << 8) | header[3]; }
            else if (payloadLen == 127) { await ReadExactAsync(stream, header, 2, 8, ct).ConfigureAwait(false); headerSize = 10; payloadLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt64(header, 2)); }

            byte[] maskingKey = new byte[4];
            if (masked) await ReadExactAsync(stream, maskingKey, 0, 4, ct).ConfigureAwait(false);

            if (payloadLen > 512 * 1024) { Debug.WriteLine($"[SpicetifyBridge] payload muito grande {payloadLen}, drop"); return; }
            var payload = new byte[payloadLen];
            if (payloadLen > 0) await ReadExactAsync(stream, payload, 0, (int)payloadLen, ct).ConfigureAwait(false);
            if (masked) for (int i = 0; i < payloadLen; i++) payload[i] ^= maskingKey[i % 4];

            // Fragmentação: se não FIN, acumula (simplificado: concatena)
            // Para Fase 1, extension manda mensagens pequenas não-fragmentadas, então trata direto.
            if (opcode == 0x1) // text
            {
                var json = Encoding.UTF8.GetString(payload);
                if (json.Trim() == "ping")
                {
                    try { await SendFrameAsync(stream, Encoding.UTF8.GetBytes("pong"), 0x1, ct).ConfigureAwait(false); } catch { }
                    continue;
                }
                TryHandleJson(json);
            }
            // se fin==false, precisaria acumular continuation — ignorado para simplicidade (raro)
            _ = fin; _ = headerSize;
        }
    }

    private static async Task<int> ReadExactAsync(NetworkStream s, byte[] buf, int off, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await s.ReadAsync(buf, off + got, count - got, ct).ConfigureAwait(false);
            if (n == 0) return got == 0 ? 0 : throw new IOException("conexão fechada");
            got += n;
        }
        return got;
    }

    private static async Task SendFrameAsync(NetworkStream s, byte[] payload, int opcode, CancellationToken ct)
    {
        // servidor → cliente: não mascarado
        var header = new List<byte>();
        header.Add((byte)(0x80 | opcode));
        if (payload.Length < 126) header.Add((byte)payload.Length);
        else if (payload.Length <= ushort.MaxValue) { header.Add(126); header.Add((byte)(payload.Length >> 8)); header.Add((byte)(payload.Length & 0xFF)); }
        else { header.Add(127); var lenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((long)payload.Length)); header.AddRange(lenBytes); }
        await s.WriteAsync(header.ToArray(), ct).ConfigureAwait(false);
        if (payload.Length > 0) await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private void TryHandleJson(string json)
    {
        SpicetifyLyricPayload? payload = null;
        try { payload = JsonSerializer.Deserialize<SpicetifyLyricPayload>(json, JsonOpts); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpicetifyBridge] JSON inválido: {ex.Message} :: {json.Substring(0, Math.Min(300, json.Length))}");
            return;
        }
        if (payload is null) return;
        if (payload.Type != "translucid-lyrics" && payload.Type != "lyrics" && payload.Lyrics.Count == 0) return;
        if (payload.Lyrics.Count == 0) return;

        LyricLine[] lines;
        try { lines = ToLyricLines(payload); }
        catch (Exception ex) { Debug.WriteLine($"[SpicetifyBridge] ToLyricLines falhou: {ex.Message}"); return; }
        if (lines.Length == 0) return;

        try { LyricsReceived?.Invoke(lines, payload); } catch (Exception ex) { Debug.WriteLine($"[SpicetifyBridge] handler falhou: {ex.Message}"); }
    }

    private static LyricLine[] ToLyricLines(SpicetifyLyricPayload p)
    {
        var outLines = new List<LyricLine>(p.Lyrics.Count);
        foreach (var l in p.Lyrics)
        {
            var start = TimeSpan.FromMilliseconds(Math.Max(0, l.StartMs));
            var end = TimeSpan.FromMilliseconds(Math.Max(l.StartMs + 1, l.EndMs));
            var text = (l.Text ?? "").Trim();
            if (text.Length == 0) continue;
            IReadOnlyList<WordSpan>? words = null;
            if (l.Words is { Count: >= 2 })
            {
                var ws = new List<WordSpan>(l.Words.Count);
                foreach (var w in l.Words)
                {
                    var wt = (w.Text ?? "").Trim();
                    if (wt.Length == 0) continue;
                    var ws0 = TimeSpan.FromMilliseconds(Math.Max(l.StartMs, w.StartMs));
                    var we = TimeSpan.FromMilliseconds(Math.Max(w.StartMs + 1, w.EndMs));
                    if (we <= ws0) we = ws0 + TimeSpan.FromMilliseconds(180);
                    ws.Add(new WordSpan(wt, ws0, we));
                }
                if (ws.Count >= 2) words = ws;
            }
            var clean = Regex.Replace(text, @"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", "").Trim();
            if (clean.Length == 0) clean = text;
            outLines.Add(new LyricLine(start, clean, end, words));
        }
        outLines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return outLines.ToArray();
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Stop();
    }
}
