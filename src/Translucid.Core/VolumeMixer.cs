using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Translucid.Core;

/// <summary>
/// Ajusta o volume POR APLICATIVO: encontra a sessão de áudio (CoreAudio) do
/// processo que está reproduzindo e muda o volume só dela — o resto do sistema
/// continua intacto.
/// </summary>
public static class VolumeMixer
{
    private const float StepPerNotch = 0.03f;

    /// <summary>Steps positivos aumentam; negativos diminuem. True se achou a sessão e aplicou.</summary>
    public static bool Adjust(string? processName, int steps)
    {
        var session = FindSession(processName);
        if (session?.SimpleAudioVolume is not { } volume)
        {
            return false;
        }

        volume.Volume = Math.Clamp(volume.Volume + steps * StepPerNotch, 0f, 1f);
        return true;
    }

    /// <summary>Volume atual da sessão (0..1) ou null se não encontrou.</summary>
    public static float? Get(string? processName) =>
        FindSession(processName)?.SimpleAudioVolume?.Volume;

    private static AudioSessionControl? FindSession(string? processName)
    {
        try
        {
            var device = new MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            if (device?.AudioSessionManager is not { Sessions: { } sessions } manager)
            {
                return null;
            }

            AudioSessionControl? fallback = null;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.IsSystemSoundsSession)
                {
                    continue;
                }

                var name = GetProcessName(session);
                if (name is null)
                {
                    continue;
                }

                fallback ??= session;
                if (processName is not null && Matches(name, processName))
                {
                    return session;
                }
            }

            // Sem correspondência por nome (app empacotado etc.): usa a única
            // sessão não-sistema disponível.
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static bool Matches(string processName, string expected)
    {
        var a = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        var b = expected.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return a == b
            || processName.Contains(b, StringComparison.OrdinalIgnoreCase)
            || expected.Contains(processName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetProcessName(AudioSessionControl session)
    {
        try
        {
            var pid = (int)session.GetProcessID;
            if (pid <= 0)
            {
                return null;
            }

            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}