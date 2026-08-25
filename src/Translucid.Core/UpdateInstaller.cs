using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Translucid.Core;

/// <summary>Estado do download reportado para a UI de progresso.</summary>
public sealed record UpdateProgress(
    UpdateProgressStage Stage,
    long BytesReceived,
    long TotalBytes,
    double BytesPerSecond,
    string Message)
{
    public static UpdateProgress Indeterminate(string message) =>
        new(UpdateProgressStage.Indeterminate, 0, 0, 0, message);
}

public enum UpdateProgressStage
{
    Indeterminate,
    Downloading,
    Verifying,
    Installing,
}

/// <summary>
/// Baixa o Translucid.zip do release, extrai no %TEMP% e instala via um .cmd
/// gerado na hora: o cmd espera o processo atual morrer, faz xcopy da pasta
/// extraída para a pasta de instalação e relança o exe (método KitLugia).
/// Reporta progresso (bytes, velocidade, estágio) via IProgress.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Executa todo o fluxo. Chame de uma Task em background.</summary>
    public static async Task InstallAsync(UpdateChecker.UpdateInfo info,
        IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var workDir = Path.Combine(
            Path.GetTempPath(), $"Translucid_Update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var zipPath = Path.Combine(workDir, "Translucid.zip");
        var extractDir = Path.Combine(workDir, "files");

        await DownloadWithProgressAsync(info.ZipUrl, zipPath, info.Tag, progress, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(info.Sha256Url))
        {
            progress?.Report(new UpdateProgress(UpdateProgressStage.Verifying, 0, 0, 0,
                "Verificando integridade…"));
            try
            {
                var shaPath = Path.Combine(workDir, "update.sha256");
                await UpdateChecker.DownloadAsync(info.Sha256Url, shaPath).ConfigureAwait(false);
                VerifySha256(zipPath, File.ReadAllText(shaPath));
            }
            catch (InvalidDataException)
            {
                throw; // checksum divergiu: não instala pacote corrompido
            }
            catch
            {
                // checksum indisponível (rede caiu baixando o .sha256): segue,
                // o zip já passou pelo download completo sem erro.
            }
        }

        progress?.Report(new UpdateProgress(UpdateProgressStage.Installing, 0, 0, 0,
            "Preparando a troca…"));
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // O zip pode trazer o exe na raiz OU dentro de uma subpasta — acha ele.
        var newExe = Directory.GetFiles(extractDir, "Translucid.exe", SearchOption.AllDirectories)
            .FirstOrDefault() ?? throw new FileNotFoundException("Translucid.zip sem Translucid.exe");

        LaunchSwapScript(Path.GetDirectoryName(newExe)!, Environment.ProcessPath!, workDir);
    }

    /// <summary>
    /// Download com progresso: amostra a velocidade a cada relatório e respeita
    /// o Content-Length quando o servidor o envia.
    /// </summary>
    private static async Task DownloadWithProgressAsync(string url, string destination,
        string tag, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        using var response = await UpdateChecker.Http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        var temp = destination + ".part";

        var sw = Stopwatch.StartNew();
        long received = 0;
        long lastReported = 0;
        double bps = 0;
        using var timer = new System.Timers.Timer(500) { AutoReset = true };
        void Report(object? s, System.Timers.ElapsedEventArgs e)
        {
            var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            var currentBps = (received - lastReported) / Math.Max(elapsed - _lastElapsed, 0.001);
            if (currentBps > 0)
            {
                bps = bps == 0 ? currentBps : bps * 0.6 + currentBps * 0.4; // suaviza
            }
            _lastElapsed = elapsed;
            lastReported = received;
            progress?.Report(new UpdateProgress(UpdateProgressStage.Downloading,
                received, total, bps, $"Baixando v{tag}…"));
        }

        timer.Elapsed += Report;
        timer.Start();
        try
        {
            await using (var http = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await http.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                }
            }
        }
        finally
        {
            timer.Stop();
            timer.Dispose();
        }

        File.Move(temp, destination, overwrite: true);

        progress?.Report(new UpdateProgress(UpdateProgressStage.Downloading,
            Math.Max(received, total), total, bps, "Download concluído"));
    }

    private static double _lastElapsed;

    /// <summary>SHA-256 do arquivo confere com o esperado ("HEX  nome" ou só HEX)?</summary>
    private static void VerifySha256(string filePath, string expectedLine)
    {
        var expected = expectedLine.Split(' ', '\t')
            .FirstOrDefault(p => p.Length == 64) ?? "";
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(filePath)));
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SHA-256 divergente: {hash[..16]}… != {expected[..16]}…");
        }
    }

    /// <summary>
    /// Gera e dispara o script de troca: espera este processo sair, LIMPA os
    /// arquivos da instalação antiga (o publish pode mudar de layout entre
    /// versões — p.ex. framework-dependent → single-file — e arquivos velhos
    /// ao lado do novo exe quebram a carga), copia os novos e relança o app.
    /// </summary>
    private static void LaunchSwapScript(string sourceDir, string runningExe, string workDir)
    {
        var pid = Environment.ProcessId;
        var installDir = Path.GetDirectoryName(runningExe)!;
        var cmdPath = Path.Combine(workDir, "Translucid_Update.cmd");

        // del: remove resíduos de layouts anteriores (deps.json, runtimeconfig,
        // Translucid.dll, lib\) que conflitam com o single-file novo. 2>nul:
        // não falha quando o arquivo não existe (instalação já era single-file).
        // xcopy /Y sobrescreve; /E desce subpastas; /Q silencioso.
        var script = $"""
@echo off
title Translucid - Atualizando...
cd /d "%~dp0"
echo Aguardando o widget fechar...
:wait
tasklist /fi "PID eq {pid}" 2>nul | findstr /i "{pid}" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)
echo Removendo arquivos antigos...
del /q "{installDir}\Translucid.dll" 2>nul
del /q "{installDir}\Translucid.deps.json" 2>nul
del /q "{installDir}\Translucid.runtimeconfig.json" 2>nul
rd /s /q "{installDir}\lib" 2>nul
echo Instalando nova versao...
xcopy "{sourceDir}" "{installDir}" /E /Y /Q /I
if errorlevel 1 (
    echo ERRO ao copiar arquivos.
    pause
    exit /b 1
)
echo Iniciando...
start "" "{runningExe}"
rd /s /q "{workDir}"
""";
        File.WriteAllText(cmdPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmdPath}\"",
            CreateNoWindow = false,
            UseShellExecute = true,
        });
    }
}
