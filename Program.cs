using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
var deploymentPath = GetDeploymentPaths();
var deployPath = deploymentPath.Path as string;
var caddyFile = deploymentPath.CaddyFile as string;
var caddyExe = deploymentPath.CaddyExe as string;

var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
cmdArgs.AddRange("--deploy", "0.3.3");

if (cmdArgs.Contains("--install"))
{
    await InstallService();
    Console.WriteLine("✅ TagDeployer service installed and started");
    return;
}

var deployIndex = cmdArgs.IndexOf("--deploy");
if (deployIndex >= 0)
{
    var targetTagName = cmdArgs[deployIndex + 1];

    var specialFolderDeploy = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    var folderDeploy = Path.Join(specialFolderDeploy, "MadAI");
    
    Directory.CreateDirectory(folderDeploy);
    var _cloneDeploy = await Cli.Wrap("gh").WithArguments("repo clone kidfearless/MadAI").WithWorkingDirectory(folderDeploy).RunAsync();
    var madaiFolderDeploy = Path.Join(folderDeploy, "MadAI");

    var allTags = await GetRepoTags(madaiFolderDeploy);
    var targetTag = allTags.FirstOrDefault(t => t.Tag == targetTagName);

    await RunAsync(targetTag);
    await Task.Delay(-1);
}


var knownTags = new HashSet<string>();
var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var folder = Path.Join(specialFolder, "MadAI");
Directory.CreateDirectory(folder);

var _1 = await Cli.Wrap("gh").WithArguments("repo clone kidfearless/MadAI").WithWorkingDirectory(folder).RunAsync();
var madaiFolder = Path.Join(folder, "MadAI");

var existingTags = (await GetRepoTags(madaiFolder)).ToHashSet();

while (true)
{
    try
    {
        var _2 = await Cli.Wrap("git").WithArguments("fetch --tags --prune").WithWorkingDirectory(madaiFolder).RunAsync();

        var tags = (await GetRepoTags(madaiFolder)).ToList();
        var newestTag = tags.First();
        var contains = existingTags.Contains(newestTag);
        existingTags = [.. tags];
        if (!contains)
        {
            await RunAsync(newestTag);
        }
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"Error in deployment loop: {ex}");
    }
    await Task.Delay(TimeSpan.FromSeconds(30));
}

async Task RunAsync(GitTag tag)
{
    var safeTag = $"{tag.Tag.Replace('.', '_')}";
    var tagPath = Path.Combine(deployPath, safeTag);

    // clone tag
    var _1 = await Cli.Wrap("gh").WithArguments($"repo clone kidfearless/MadAI \"{tagPath}\"").RunAsync();
    var git = Cli.Wrap("git").WithWorkingDirectory(tagPath);
    var _2 = await git.WithArguments($"checkout tags/{tag.Tag}").RunAsync();

    var projectPath = Path.Combine(tagPath, "Sitev2", "MadAI.API");
    var pipeOut = PipeTarget.ToDelegate(Console.WriteLine);
    var pipeErr = PipeTarget.ToDelegate(Console.Error.WriteLine);

    // build rust with shared target dir so dependencies are cached across deployments
    var cargoTargetDir = Path.Combine(deployPath, "cargo-cache");
    var rustPath = Path.Combine(tagPath, "PdfInterop");
    var _3 = await Cli.Wrap("cargo")
        .WithArguments("build --release")
        .WithWorkingDirectory(rustPath)
        .WithEnvironmentVariables(e => e.Set("CARGO_TARGET_DIR", cargoTargetDir))
        .RunAsync();

    // copy built dll to where the csproj expects it
    var rustDll = Path.Combine(cargoTargetDir, "release", "pdf_interop.dll");
    var rustDllDest = Path.Combine(rustPath, "target", "release");
    Directory.CreateDirectory(rustDllDest);
    if (File.Exists(rustDll))
    {
        File.Copy(rustDll, Path.Combine(rustDllDest, "pdf_interop.dll"), true);
    }

    // publish api, skip rust rebuild since we already built it
    var dotnet = Cli.Wrap("dotnet").WithWorkingDirectory(projectPath);
    var _4 = await dotnet.WithArguments("publish /p:SkipRustBuild=true").RunAsync();

    // handle frontend and extension builds
    var reactPath = Path.Combine(tagPath, "Sitev2", "MadAI.React");
    var npm = Cli.Wrap("npm");
    var _6 = await npm.WithArguments("install").WithWorkingDirectory(reactPath).RunAsync();
    var _7 = await npm.WithArguments("run build").WithWorkingDirectory(reactPath).RunAsync();

    // copy react dist to wwwroot where caddy expects it
    var distPath = Path.Combine(reactPath, "dist");
    var wwwroot = Path.Combine(tagPath, "wwwroot");
    if (Directory.Exists(distPath))
    {
        CopyDirectory(distPath, wwwroot);
    }

    var extensionPath = Path.Combine(tagPath, "Extension");
    var _8 = await npm.WithArguments("install").WithWorkingDirectory(extensionPath).RunAsync();
    var _9 = await npm.WithArguments("run build:zip").WithWorkingDirectory(extensionPath).RunAsync();

    // Copy Caddyfile to deployment folder
    var destCaddyfile = Path.Combine(tagPath, "Caddyfile");
    File.Copy(caddyFile, destCaddyfile, true);

    // set up API
    var publishOutput = Path.Combine(projectPath, "bin", "Release", "net10.0", "publish");
    await KillProcess("MadAI.API");
    var apiExePath = Path.Combine(publishOutput, "MadAI.API.exe");
    _ = Cli.Wrap(apiExePath)
        .WithWorkingDirectory(publishOutput)
        .RunAsync();

    await KillProcess("caddy");
    _ = Cli.Wrap(caddyExe)
        .WithArguments($"run --config \"{destCaddyfile}\"")
        .WithWorkingDirectory(tagPath)
        .RunAsync();
}

static async Task<HashSet<GitTag>> GetRepoTags(string madaiFolder)
{
    var tagsRaw = await Cli.Wrap("git")
        .WithArguments("""log --tags --simplify-by-decoration --pretty="%ai %d" """)
        .WithWorkingDirectory(madaiFolder)
        .ExecuteBufferedAsync();
    var matches = Regex.Matches(tagsRaw.StandardOutput, @"^([\d\- :+]+)\s+.*tag:\s*([^,\s]+)", RegexOptions.Multiline)
        .Select(m => new GitTag(DateTimeOffset.Parse(m.Groups[1].Value), m.Groups[2].Value))
        .Where(t => !string.IsNullOrEmpty(t.Tag) && t.TagDate != default)
        .OrderByDescending(t => t.TagDate)
        .ToHashSet();

    return matches;
}

static async Task KillProcess(string name)
{
    var _1 = await Cli.Wrap("taskkill")
        .WithArguments($"/F /T /FI \"IMAGENAME eq {name}*\"")
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source))
    {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
    }
    foreach (var dir in Directory.GetDirectories(source))
    {
        CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}

static async Task InstallService()
{
    var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    // Create service
    var _1 = await Cli.Wrap("sc")
        .WithArguments($"create TagDeployer start=auto binPath=\"{exePath}\" DisplayName=\"MadAI Tag Deployer\"")
        .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

    // Start service automatically
    var _2 = await Cli.Wrap("sc")
        .WithArguments("start TagDeployer")
        .WithValidation(CommandResultValidation.None).RunAsync();
}

static DeploymentPath GetDeploymentPaths()
{
    var root = Path.Combine("D:", "Deployments");
    var caddyFile = Path.Combine(root, "CaddyFile");
    var caddy = Path.Combine(root, "caddy.exe");

    return new(root, caddyFile, caddy);
}

static class E
{
    extension(Command task)
    {
        public async Task<BufferedCommandResult> ExecuteBufferedAsync()
        {
            Console.WriteLine($"{task.TargetFilePath} {task.Arguments}");
            var result = await BufferedCommandExtensions.ExecuteBufferedAsync(task);
            Console.WriteLine(result.StandardOutput);
            Console.Error.WriteLine(result.StandardError);
            return result;
        }
        public async Task<CommandResult> RunAsync()
        {
            Console.WriteLine($"{task.TargetFilePath} {task.Arguments}");
            var result = await task
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(
                    PipeTarget.ToDelegate(Console.WriteLine))
                .WithStandardErrorPipe(
                    PipeTarget.ToDelegate(Console.Error.WriteLine))
            .ExecuteAsync(default);

            return result;
        }
    }
}
