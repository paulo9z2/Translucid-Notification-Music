using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Translucid.Core;

/// <summary>Uma linha de letra com o instante em que ela deve aparecer.</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>
/// Busca letras sincronizadas (LRC) na API pública do LRCLIB (lrclib.net) —
/// a mesma fonte usada por plugins como o Spicetify Lyrics.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly ConcurrentDictionary<string, LyricLine[]> Cache = new();
    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);

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
        if (doc.RootElement.TryGetProperty("syncedLyrics", out var lrc) && lrc.ValueKind == JsonValueKind.String)
        {
            return ParseLrc(lrc.GetString() ?? string.Empty);
        }

        return null;
    }

    private static LyricLine[]? ParseLrc(string lrc)
    {
        var list = new List<LyricLine>();
        TimeSpan? last = null;

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

            var time = TimeSpan.FromSeconds(minutes * 60 + seconds);
            if (last == time)
            {
                continue;
            }

            last = time;
            list.Add(new LyricLine(time, text));
        }

        return list.Count == 0 ? null : list.OrderBy(l => l.Time).ToArray();
    }
}