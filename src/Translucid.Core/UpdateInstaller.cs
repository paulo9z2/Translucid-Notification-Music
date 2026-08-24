using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Translucid.Core;

/// <summary>
/// Baixa o Translucid.zip do release, extrai no %TEMP% e instala via um .cmd
/// gerado na hora: o cmd espera o processo atual morrer, faz xcopy da pasta
/// extraída para a pasta de instalação e relança o exe (método KitLugia).
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Executa todo o fluxo. Chame de uma Task em background.</summary>
    public static async Task InstallAsync(UpdateChecker.UpdateInfo info)
    {
        var workDir = Path.Combine(
            Path.GetTempPath(), $"Translucid_Update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var zipPath = Path.Combine(workDir, "Translucid.zip");
        var extractDir = Path.Combine(workDir, "files");

        await UpdateChecker.DownloadAsync(info.ZipUrl, zipPath).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(info.Sha256Url))
        {
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

        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // O zip pode trazer o exe na raiz OU dentro de uma subpasta — acha ele.
        var newExe = Directory.GetFiles(extractDir, "Translucid.exe", SearchOption.AllDirectories)
            .FirstOrDefault() ?? throw new FileNotFoundException("Translucid.zip sem Translucid.exe");

        LaunchSwapScript(Path.GetDirectoryName(newExe)!, Environment.ProcessPath!, workDir);
    }

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
    /// Gera e dispara o script de troca: espera este processo sair, copia os
    /// arquivos novos por cima e relança o app.
    /// </summary>
    private static void LaunchSwapScript(string sourceDir, string runningExe, string workDir)
    {
        var pid = Environment.ProcessId;
        var installDir = Path.GetDirectoryName(runningExe)!;
        var cmdPath = Path.Combine(workDir, "Translucid_Update.cmd");

        // xcopy /Y sobrescreve; /E desce subpastas (lib\); /Q silencioso.
        // rd no final apaga a área de trabalho temporária desta atualização.
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
