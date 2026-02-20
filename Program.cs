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

var deployPath = "E:\\Deployments";
var caddyFile = "E:\\Deployments\\CaddyFile";

var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (cmdArgs.Contains("--install"))
{
	await InstallService();
	Console.WriteLine("✅ TagDeployer service installed and started");
	return;
}

var knownTags = new HashSet<string>();
var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var folder = Path.Join(specialFolder, "MadAI");
Directory.CreateDirectory(folder);

await Cli.Wrap("gh").WithArguments("clone kidfearless/MadAI").WithWorkingDirectory(folder).ExecuteAsync();
var madaiFolder = Path.Join(folder, "MadAI");

var existingTags = (await GetRepoTags(madaiFolder)).ToHashSet();

while (true)
{
	try
	{
		await Cli.Wrap("git").WithArguments("fetch --tags --prune").WithWorkingDirectory(madaiFolder).ExecuteAsync();

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
	await Cli.Wrap("gh").WithArguments($"clone kidfearless/MadAI \"{tagPath}\"").ExecuteAsync();
	await git.WithArguments($"checkout {tag}").ExecuteAsync();

	var projectPath = Path.Combine(tagPath, "Sitev2/MadAI.API");
	var dotnet = Cli.Wrap("dotnet").WithWorkingDirectory(projectPath);

	// publish api
	await dotnet.WithArguments("restore").ExecuteAsync();
	await dotnet.WithArguments("publish").ExecuteAsync();

	// set up API
	var apiFolder = Path.Combine(tagPath, "Api", safeTag);
	var publishOutput = Path.Combine(projectPath, "bin", "Release", "net10.0", "publish");
	Directory.Move(publishOutput, apiFolder);

	await KillProcess("MadAI.API");
	var apiExePath = Path.Combine(apiFolder, "MadAI.API");
	_ = Cli.Wrap(apiExePath).WithWorkingDirectory(apiFolder).ExecuteAsync();

	// handle frontend and extension builds
	var reactPath = Path.Combine(tagPath, "Sitev2", "MadAI.React");
	await Cli.Wrap("npm").WithArguments("install").WithWorkingDirectory(reactPath).ExecuteAsync();
	await Cli.Wrap("npm").WithArguments("run build").WithWorkingDirectory(reactPath).ExecuteAsync();

	var extensionPath = Path.Combine(tagPath, "Extension");
	await Cli.Wrap("npm").WithArguments("install").WithWorkingDirectory(extensionPath).ExecuteAsync();
	await Cli.Wrap("npm").WithArguments("run build:zip").WithWorkingDirectory(extensionPath).ExecuteAsync();

	// Copy Caddyfile to deployment folder
	var destCaddyfile = Path.Combine(tagPath, "Caddyfile");
	File.Copy(caddyFile, destCaddyfile, true);

	await KillProcess("caddy");
	_ = Cli.Wrap("caddy").WithArguments($"run --config \"{destCaddyfile}\"").WithWorkingDirectory(tagPath).ExecuteAsync();
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
		await Cli.Wrap("taskkill")
			.WithArguments($"/F /T /FI \"IMAGENAME eq {name}*\"")
			.WithValidation(CommandResultValidation.None)
			.ExecuteAsync();
	}
	else
	{
		await Cli.Wrap("pkill")
			.WithArguments($"-f {name}")
			.WithValidation(CommandResultValidation.None)
			.ExecuteAsync();
	}
}

static async Task InstallService()
{
	var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

	// Create service
	await Cli.Wrap("sc")
		.WithArguments($"create TagDeployer start=auto binPath=\"{exePath}\" DisplayName=\"MadAI Tag Deployer\"")
		.ExecuteAsync();

	// Start service automatically
	await Cli.Wrap("sc")
		.WithArguments("start TagDeployer")
		.ExecuteAsync();
}
