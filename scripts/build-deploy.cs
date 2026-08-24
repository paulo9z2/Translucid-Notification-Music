// build-deploy.cs — deploy 100% C# (dotnet run --file), sem PowerShell.
//
//   dotnet run --file scripts/build-deploy.cs -- <versão>   ex.: 1.4.0
//
// Faz tudo que o Deploy.ps1 fazia: publish single-file self-contained,
// monta Publish/Translucid.zip e gera o .sha256.
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

var version = args.Length > 0 ? args[0] : throw new InvalidOperationException("Uso: dotnet run --file scripts/build-deploy.cs -- <versao>");
if (!Version.TryParse(version, out _))
{
    throw new InvalidOperationException($"Versão inválida: {version} (esperado X.Y.Z)");
}

var root = FindRoot();
var dist = Path.Combine(root, "dist", "Translucid");
var publish = Path.Combine(root, "Publish");
Directory.CreateDirectory(publish);

Console.WriteLine($"[1/3] publish single-file v{version}...");
var psi = new ProcessStartInfo("dotnet",
    $"publish src/Translucid.App -c Release -r win-x64 --self-contained true " +
    $"-p:PublishSingleFile=true -p:Version={version} -o \"{dist}\"")
{
    WorkingDirectory = root,
};
using var publishProcess = Process.Start(psi)!;
publishProcess.WaitForExit();
var exit = publishProcess.ExitCode;
if (exit != 0)
{
    Console.Error.WriteLine($"ERRO: dotnet publish saiu com código {exit}");
    return exit;
}

Console.WriteLine("[2/3] montando Translucid.zip...");
var exe = Path.Combine(dist, "Translucid.exe");
if (!File.Exists(exe))
{
    Console.Error.WriteLine("ERRO: publish não gerou Translucid.exe");
    return 1;
}

// DLLs nativas do WPF que o single-file NÃO embute.
var zipEntries = new List<string> { exe };
zipEntries.AddRange(Directory.GetFiles(dist, "*_cor3.dll"));
zipEntries.Add(Path.Combine(root, "README.md"));
var setupTerminal = Path.Combine(root, "scripts", "setup-terminal.ps1");
if (File.Exists(setupTerminal))
{
    zipEntries.Add(setupTerminal);
}
foreach (var f in zipEntries.Where(f => !File.Exists(f)))
{
    Console.Error.WriteLine($"ERRO: faltando {f}");
    return 1;
}

var zipPath = Path.Combine(publish, "Translucid.zip");
await using (var stream = File.Create(zipPath))
using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
{
    foreach (var file in zipEntries)
    {
        zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
    }
}

Console.WriteLine("[3/3] SHA-256...");
string sha;
await using (var fs = File.OpenRead(zipPath))
{
    sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.Create().ComputeHashAsync(fs));
}
await File.WriteAllTextAsync(zipPath + ".sha256", $"{sha}  Translucid.zip\n");

Console.WriteLine($"===== Deploy concluído (v{version}) =====");
Console.WriteLine($"Zip:      {zipPath} ({new FileInfo(zipPath).Length:N0} bytes)");
Console.WriteLine($"SHA-256:  {sha}");

return 0;

// Sobe até achar a pasta que contém a solução (.slnx). Com `dotnet run --file`,
// o BaseDirectory é o cache do runner — usa o caminho do próprio script.
static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && dir.GetFiles("*.slnx").Length == 0)
    {
        dir = dir.Parent!;
    }

    if (dir is not null)
    {
        return dir.FullName;
    }

    dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null && dir.GetFiles("*.slnx").Length == 0)
    {
        dir = dir.Parent!;
    }

    return dir?.FullName ?? throw new InvalidOperationException("Raiz da solução não encontrada");
}
