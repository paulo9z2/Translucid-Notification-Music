using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Translucid.Core;

/// <summary>Uma palavra da linha com o instante exato em que é cantada (LRC estendido).</summary>
public sealed record WordSpan(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// Uma linha de letra: instante inicial, texto e (opcionalmente) marcações
/// por palavra do LRC estendido — &lt;mm:ss.xx&gt; antes de cada palavra,
/// como no karaokê estilo Spicy Lyrics.
/// </summary>
public sealed record LyricLine(TimeSpan Time, string Text, TimeSpan End, IReadOnlyList<WordSpan>? Words);

/// <summary>
/// Busca letras sincronizadas (LRC) na API pública do LRCLIB (lrclib.net) —
/// a mesma fonte usada por plugins como o Spicetify Lyrics.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly ConcurrentDictionary<string, LyricLine[]> Cache = new();
    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"<(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)>", RegexOptions.Compiled);

    static LyricsService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Translucid/1.0");
    }

    /// <summary>Letras sincronizadas da faixa, ou null se não houver.</summary>
    public static async Task<LyricLine[]?> GetAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var key = $"{title}\u0001{artist}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        LyricLine[]? lines = null;
        var networkError = false;

        try
        {
            lines = await FetchAsync($"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}"
                + (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}"))
                .ConfigureAwait(false);

            // o /get retorna 404 quando há várias versões da música; tenta o /search
            lines ??= await FetchSearchAsync(title, artist).ConfigureAwait(false);
        }
        catch
        {
            networkError = true;
        }

        // Só cacheia "não encontrada" quando a resposta foi limpa (404/completa).
        // Falha de rede fica fora do cache para a próxima busca tentar de novo.
        if (!networkError)
        {
            Cache.TryAdd(key, lines ?? Array.Empty<LyricLine>());
        }

        return lines;
    }

    private static async Task<LyricLine[]?> FetchSearchAsync(string title, string artist)
    {
        var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}"
            + (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");

        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("syncedLyrics", out var lrc) && lrc.ValueKind == JsonValueKind.String)
            {
                var lines = ParseLrc(lrc.GetString() ?? string.Empty);
                if (lines is { Length: > 0 })
                {
                    return lines;
                }
            }
        }

        return null;
    }

    private static async Task<LyricLine[]?> FetchAsync(string url)
    {
        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;

        // Preferência: lyricsfile (YAML com start_ms/end_ms REAIS por linha,
        // mais preciso que LRC, cujo "fim" é inferido da próxima linha).
        if (root.TryGetProperty("lyricsfile", out var lf) && lf.ValueKind == JsonValueKind.String)
        {
            var lines = ParseLrcFile(lf.GetString() ?? string.Empty);
            if (lines is { Length: > 0 })
            {
                return lines;
            }
        }

        if (root.TryGetProperty("syncedLyrics", out var lrc) && lrc.ValueKind == JsonValueKind.String)
        {
            return ParseLrc(lrc.GetString() ?? string.Empty);
        }

        return null;
    }

    /// <summary>
    /// Parser do lyricsfile da LRCLIB (YAML mínimo: lines com text/start_ms/end_ms).
    /// </summary>
    private static LyricLine[]? ParseLrcFile(string yaml)
    {
        var lines = new List<(TimeSpan Start, TimeSpan End, string Text)>();
        string? currentText = null;
        TimeSpan currentStart = default;

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("- text:", StringComparison.Ordinal))
            {
                currentText = line["- text:".Length..].Trim().Trim('\'', '"');
            }
            else if (currentText is not null && line.StartsWith("start_ms:", StringComparison.Ordinal))
            {
                _ = int.TryParse(line["start_ms:".Length..].Trim(), out var ms);
                currentStart = TimeSpan.FromMilliseconds(ms);
            }
            else if (currentText is not null && line.StartsWith("end_ms:", StringComparison.Ordinal))
            {
                if (int.TryParse(line["end_ms:".Length..].Trim(), out var me))
                {
                    lines.Add((currentStart, TimeSpan.FromMilliseconds(me), currentText));
                }
                currentText = null;
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var result = new List<LyricLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var (time, end, text) = lines[i];
            var words = ParseWords(text, time, end);
            var clean = WordTag.Replace(text, "").Trim();
            if (clean.Length == 0)
            {
                continue;
            }

            result.Add(new LyricLine(time, clean, end, words));
        }

        return result.Count == 0 ? null : result.ToArray();
    }

    private static LyricLine[]? ParseLrc(string lrc)
    {
        var raw = new List<(TimeSpan Time, string Text)>();

        foreach (Match m in TimeTag.Matches(lrc))
        {
            var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(
                m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);

            var start = m.Index + m.Length;
            var end = lrc.IndexOf('\n', start);
            var text = (end < 0 ? lrc[start..] : lrc[start..end]).Trim();
            if (text.Length == 0 || text.StartsWith("[", StringComparison.Ordinal))
            {
                continue;
            }

            raw.Add((TimeSpan.FromSeconds(minutes * 60 + seconds), text));
        }

        if (raw.Count == 0)
        {
            return null;
        }

        var ordered = raw.OrderBy(r => r.Time).ToList();

        var result = new List<LyricLine>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (time, text) = ordered[i];
            var endTime = i + 1 < ordered.Count ? ordered[i + 1].Time : time + TimeSpan.FromSeconds(5);

            // LRC estendido: <mm:ss.xx> marca o início de cada palavra (karaokê).
            var words = ParseWords(text, time, endTime);
            var clean = WordTag.Replace(text, "").Trim();
            if (clean.Length == 0)
            {
                continue;
            }

            result.Add(new LyricLine(time, clean, endTime, words));
        }

        return result.Count == 0 ? null : result.ToArray();
    }

    /// <summary>Extrai marcações &lt;mm:ss.xx&gt; por palavra; null se houver menos de duas.</summary>
    private static IReadOnlyList<WordSpan>? ParseWords(string text, TimeSpan lineStart, TimeSpan lineEnd)
    {
        var marks = WordTag.Matches(text);
        if (marks.Count < 2)
        {
            return null;
        }

        var words = new List<WordSpan>(marks.Count);
        for (var i = 0; i < marks.Count; i++)
        {
            var m = marks[i];
            var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(
                m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
            var start = TimeSpan.FromSeconds(minutes * 60 + seconds);

            var chunkEnd = i + 1 < marks.Count ? marks[i + 1].Index : text.Length;
            var wordText = text[m.Index..chunkEnd]
                .Replace(WordTag.Replace(m.Value, ""), "")
                .Trim();
            if (wordText.Length == 0)
            {
                continue;
            }

            var end = i + 1 < marks.Count
                ? TimeSpan.FromSeconds(
                    int.Parse(marks[i + 1].Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                    double.Parse(marks[i + 1].Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture))
                : lineEnd;

            words.Add(new WordSpan(wordText, start < lineStart ? lineStart : start, end));
        }

        return words.Count >= 2 ? words : null;
    }
}
