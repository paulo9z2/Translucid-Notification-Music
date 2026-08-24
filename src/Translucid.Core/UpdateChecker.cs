using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Translucid.Core;

/// <summary>
/// Verifica se existe release nova no GitHub e expõe os links de download.
/// Lê api.github.com/.../releases/latest e compara o tag_name com a versão local.
/// </summary>
public static class UpdateChecker
{
    private const string LatestUrl =
        "https://api.github.com/repos/paulo9z2/Translucid-Notification-Music/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static UpdateChecker()
    {
        // A API do GitHub EXIGE User-Agent; sem ele responde 403.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Translucid/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Release disponível para baixar (zip + checksum).</summary>
    public sealed record UpdateInfo(string Tag, string ZipUrl, string Sha256Url);

    /// <summary>
    /// Info da atualização pendente, ou null se está atualizado / sem rede /
    /// release sem zip anexado.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion)
    {
        try
        {
            using var response = await Http.GetAsync(LatestUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var cleanTag = tag.TrimStart('v', 'V');
            if (!IsNewer(cleanTag, currentVersion))
            {
                return null;
            }

            string? zipUrl = null, shaUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString()
                        : null;
                    if (name is null || url is null)
                    {
                        continue;
                    }

                    if (name.Equals("Translucid.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = url;
                    }
                    else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        shaUrl = url;
                    }
                }
            }

            return zipUrl is null ? null : new UpdateInfo(cleanTag, zipUrl, shaUrl ?? "");
        }
        catch
        {
            // Sem internet / rate limit / API fora: segue a vida, tenta no próximo boot.
            return null;
        }
    }

    /// <summary>Comparação numérica por parte: 1.10.0 &gt; 1.9.9 (string compararia errado).
    /// Ignora sufixo de commit (ex.: "1.2.2+1c325ee").</summary>
    internal static bool IsNewer(string latest, string current)
    {
        var a = Parse(latest);
        var b = Parse(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        return false;
    }

    private static int[] Parse(string version) =>
        version.Split('+')[0] // "1.2.2+1c325ee" → "1.2.2" (InformationalVersion traz o commit)
            .Split('.', '-', ' ')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();

    /// <summary>Baixa um arquivo para o caminho indicado.</summary>
    public static async Task DownloadAsync(string url, string destination)
    {
        using var response = await Http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var temp = destination + ".part";
        await using (var http = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var file = File.Create(temp))
        {
            await http.CopyToAsync(file).ConfigureAwait(false);
        }

        File.Move(temp, destination, overwrite: true);
    }
}
