// Translucid.Deploy — tooling de release 100% C#.
//
//   dotnet run --project src/Translucid.Deploy -- <versao>          # build + zip + sha256
//   dotnet run --project src/Translucid.Deploy -- <versao> --release # acima + GitHub release + tag
//
// Substitui Deploy.ps1/make-icon.ps1/setup-terminal.ps1: nada de PowerShell
// ou scripts soltos na pasta — tudo compila junto com a solução.
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

if (args.Length == 0 || !Version.TryParse(args[0], out var version))
{
    Console.WriteLine("Uso: dotnet run --project src/Translucid.Deploy -- <versao> [--release]");
    return 1;
}

var makeRelease = args.Contains("--release", StringComparer.OrdinalIgnoreCase);
var root = FindRoot();
var dist = Path.Combine(root, "dist", "Translucid");
var publishDir = Path.Combine(root, "Publish");
Directory.CreateDirectory(publishDir);
var versionString = version.ToString(3); // 1.3 -> 1.3.0

// ------------------------------------------------------------------ build

Console.WriteLine($"[1/4] publish single-file v{versionString}...");
var exit = Run("dotnet", new[] { "publish", "src/Translucid.App",
    "-c", "Release", $"-p:Version={versionString}", "-o", dist });
if (exit != 0)
{
    Console.Error.WriteLine($"ERRO: dotnet publish saiu com código {exit}");
    return exit;
}

var exe = Path.Combine(dist, "Translucid.exe");
if (!File.Exists(exe))
{
    Console.Error.WriteLine("ERRO: publish não gerou Translucid.exe");
    return 1;
}

// ------------------------------------------------------------------- zip

Console.WriteLine("[2/4] montando Publish/Translucid.zip...");
var zipPath = Path.Combine(publishDir, "Translucid.zip");

// O exe é single-file com IncludeNativeLibrariesForSelfExtract=true: as DLLs
// nativas do WPF vão EMBUTIDAS, então o zip leva só o essencial.
var zipFiles = new List<string> { exe };
foreach (var optional in new[] { "README.md", Path.Combine("scripts", "setup-terminal.ps1") })
{
    var full = Path.Combine(root, optional);
    if (File.Exists(full))
    {
        zipFiles.Add(full);
    }
}

await using (var stream = File.Create(zipPath))
using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
{
    foreach (var file in zipFiles)
    {
        zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
    }
}

Console.WriteLine("[3/4] SHA-256...");
await using var fs = File.OpenRead(zipPath);
var sha = Convert.ToHexString(await SHA256.Create().ComputeHashAsync(fs));
await using var shaFs = File.Create(zipPath + ".sha256");
await shaFs.WriteAsync(Encoding.ASCII.GetBytes($"{sha}  Translucid.zip\n"));

Console.WriteLine($"[4/4] pacote pronto: {new FileInfo(zipPath).Length:N0} bytes | sha256 {sha[..16]}…");

// --------------------------------------------------------------- release

if (!makeRelease)
{
    Console.WriteLine("===== Concluído (sem --release; pacote em Publish/) =====");
    return 0;
}

Console.WriteLine($"[gh] release v{versionString}...");
exit = Run("gh", new[]
{
    "release", "create", $"v{versionString}",
    "--title", $"Translucid v{versionString}",
    "--generate-notes",
    zipPath, zipPath + ".sha256",
}, ignoreFailure: true);
if (exit != 0)
{
    // Release/tag já existem: recria por cima.
    Run("gh", new[] { "release", "delete", $"v{versionString}", "--yes" }, ignoreFailure: true);
    exit = Run("gh", new[]
    {
        "release", "create", $"v{versionString}",
        "--title", $"Translucid v{versionString}",
        "--generate-notes",
        zipPath, zipPath + ".sha256",
    });
    if (exit != 0)
    {
        Console.Error.WriteLine("ERRO ao criar release no GitHub.");
        return exit;
    }
}

Console.WriteLine("[git] commit + push + tag...");
Run("git", new[] { "add", "-A", "src", "DOSSIER.md", "README.md", "deploy.bat",
    "Directory.Build.props", "Directory.Packages.props" }, ignoreFailure: true);
Run("git", new[] { "commit", "-m", $"Deploy v{versionString}", "--allow-empty" }, ignoreFailure: true);
Run("git", new[] { "push", "origin", "main" }, ignoreFailure: true);
Run("git", new[] { "tag", "-f", $"v{versionString}" }, ignoreFailure: true);
Run("git", new[] { "push", "origin", $"v{versionString}", "--force" }, ignoreFailure: true);

Console.WriteLine($"===== v{versionString} publicada =====");
return 0;

// ----------------------------------------------------------------- utils

static int Run(string fileName, IReadOnlyList<string> arguments, bool ignoreFailure = false)
{
    var psi = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = FindRoot(),
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    foreach (var a in arguments)
    {
        psi.ArgumentList.Add(a); // quoting correto mesmo com espaços no caminho
    }

    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"não iniciou: {fileName}");
    var output = p.StandardOutput.ReadToEnd();
    var err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (output.Length > 0)
    {
        Console.Write(output);
    }
    if (p.ExitCode != 0 && !ignoreFailure)
    {
        Console.Error.WriteLine($"ERRO: {fileName} {arguments[0]} saiu com código {p.ExitCode}");
        if (!string.IsNullOrWhiteSpace(err))
        {
            Console.Error.WriteLine(err);
        }
    }
    return p.ExitCode;
}
static string FindRoot()
{
    // Sobe da pasta do executável/working dir até achar a solução.
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent!;
        }
    }
    throw new InvalidOperationException("Raiz da solução não encontrada");
}
