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

var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (cmdArgs.Contains("--install"))
{
	await InstallService();
	Console.WriteLine("✅ TagDeployer service installed and started");
	return;
}

var deployIndex = Array.IndexOf(cmdArgs, "--deploy");
if (deployIndex >= 0)
{
	var targetTagName = cmdArgs[deployIndex + 1];

	var specialFolderDeploy = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
	var folderDeploy = Path.Join(specialFolderDeploy, "MadAI");
	Directory.CreateDirectory(folderDeploy);
	var _cloneDeploy = await Cli.Wrap("gh").WithArguments("repo clone kidfearless/MadAI").WithWorkingDirectory(folderDeploy).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
	var madaiFolderDeploy = Path.Join(folderDeploy, "MadAI");

	var allTags = await GetRepoTags(madaiFolderDeploy);
	var targetTag = allTags.FirstOrDefault(t => t.Tag == targetTagName);

	await ExecuteAsync(targetTag);
	return;
}


var knownTags = new HashSet<string>();
var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var folder = Path.Join(specialFolder, "MadAI");
Directory.CreateDirectory(folder);

var _1 = await Cli.Wrap("gh").WithArguments("repo clone kidfearless/MadAI").WithWorkingDirectory(folder).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
var madaiFolder = Path.Join(folder, "MadAI");

var existingTags = (await GetRepoTags(madaiFolder)).ToHashSet();

while (true)
{
	try
	{
		var _2 = await Cli.Wrap("git").WithArguments("fetch --tags --prune").WithWorkingDirectory(madaiFolder).ExecuteBufferedAsync();

		var tags = (await GetRepoTags(madaiFolder)).ToList();
		var newestTag = tags.First();
		var contains = existingTags.Contains(newestTag);
		existingTags = [.. tags];
		if (!contains)
		{
			await ExecuteAsync(newestTag);
		}
	}
	catch (Exception ex)
	{
		await Console.Error.WriteLineAsync($"Error in deployment loop: {ex}");
	}
	await Task.Delay(TimeSpan.FromMinutes(1));
}

async Task ExecuteAsync(GitTag tag)
{
	var safeTag = $"{tag.Tag.Replace('.', '_')}";
	var tagPath = Path.Combine(deployPath, safeTag);
	var git = Cli.Wrap("git").WithWorkingDirectory(deployPath);

	// clone tag
	var _1 = await Cli.Wrap("gh").WithArguments($"repo clone kidfearless/MadAI \"{tagPath}\"").WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
	var _2 = await git.WithArguments($"checkout {tag}").WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

	var projectPath = Path.Combine(tagPath, "Sitev2/MadAI.API");
	var dotnet = Cli.Wrap("dotnet").WithWorkingDirectory(projectPath);

	// publish api
	var _3 = await dotnet.WithArguments("restore").WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
	var _4 = await dotnet.WithArguments("publish").WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

	// set up API
	var apiFolder = Path.Combine(tagPath, "Api", safeTag);
	var publishOutput = Path.Combine(projectPath, "bin", "Release", "net10.0", "publish");
	Directory.Move(publishOutput, apiFolder);

	await KillProcess("MadAI.API");
	var apiExePath = Path.Combine(apiFolder, "MadAI.API");
	var _5 = Cli.Wrap(apiExePath).WithWorkingDirectory(apiFolder).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

	// handle frontend and extension builds
	var reactPath = Path.Combine(tagPath, "Sitev2", "MadAI.React");
	var _6 = await Cli.Wrap("npm").WithArguments("install").WithWorkingDirectory(reactPath).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
	var _7 = await Cli.Wrap("npm").WithArguments("run build").WithWorkingDirectory(reactPath).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

	var extensionPath = Path.Combine(tagPath, "Extension");
	var _8 = await Cli.Wrap("npm").WithArguments("install").WithWorkingDirectory(extensionPath).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
	var _9 = await Cli.Wrap("npm").WithArguments("run build:zip").WithWorkingDirectory(extensionPath).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

	// Copy Caddyfile to deployment folder
	var destCaddyfile = Path.Combine(tagPath, "Caddyfile");
	File.Copy(caddyFile, destCaddyfile, true);

	await KillProcess("caddy");
	var _10 = Cli.Wrap("caddy").WithArguments($"run --config \"{destCaddyfile}\"").WithWorkingDirectory(tagPath).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
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
	if (OperatingSystem.IsWindows())
	{
		var _1 = await Cli.Wrap("taskkill")
			.WithArguments($"/F /T /FI \"IMAGENAME eq {name}*\"")
			.WithValidation(CommandResultValidation.None)
			.ExecuteBufferedAsync();
	}
	else
	{
		var _2 = await Cli.Wrap("pkill")
			.WithArguments($"-f {name}")
			.WithValidation(CommandResultValidation.None)
			.ExecuteBufferedAsync();
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
		.WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
}

static DeploymentPath GetDeploymentPaths()
{
	if (OperatingSystem.IsLinux())
	{
		var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var linuxDeployPath = Path.Combine(appDataPath, "TagDeployer", "Deployments");
		var linuxCaddyFile = Path.Combine(linuxDeployPath, "Caddyfile");
		return new(linuxDeployPath, linuxCaddyFile);
	}

	return new("E:\\Deployments", "E:\\Deployments\\CaddyFile");
}

static class E
{
	extension(Command task)
	{
		public async Task<BufferedCommandResult> ExecuteBufferedAsync()
		{
			var result = await BufferedCommandExtensions.ExecuteBufferedAsync(task);
			Console.WriteLine($"{task.TargetFilePath} {task.Arguments}");
			Console.WriteLine(result.StandardOutput);
			Console.Error.WriteLine(result.StandardOutput);
			return result;
		}
	}
}
