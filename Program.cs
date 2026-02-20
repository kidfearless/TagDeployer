using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;

var deployPath = "E:\\Deployments";
Directory.CreateDirectory(deployPath);

var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (cmdArgs.Contains("--install"))
{
	await InstallService();
	return;
}
else if (cmdArgs.Contains("--uninstall"))
{
	await UninstallService();
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
		existingTags = tags.ToHashSet();
		if (!contains)
		{
			await ExecuteAsync(newestTag);
		}
	}
	catch (Exception ex)
	{
		await Console.Error.WriteLineAsync($"Error in deployment loop: {ex.Message}");
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

	await KillProcess("caddy");
	_ = Cli.Wrap("caddy").WithArguments("run --config Caddyfile").ExecuteAsync();
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
	Console.WriteLine("Installing TagDeployer Windows Service...");

	var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
	var args = $"create TagDeployer start=auto binPath=\"{exePath}\" DisplayName=\"MadAI Tag Deployer\"";

	try
	{
		using var process = Process.Start("sc", args);
		if (process != null)
		{
			await process.WaitForExitAsync();
		}
		Console.WriteLine("✅ TagDeployer service installed");
		Console.WriteLine("Start: sc start TagDeployer");
		Console.WriteLine("Stop: sc stop TagDeployer");
	}
	catch (Exception ex)
	{
		Console.WriteLine($"❌ Service install failed: {ex.Message}");
		Console.WriteLine("Ensure running as Administrator");
	}
}

static async Task UninstallService()
{
	Console.WriteLine("Uninstalling TagDeployer Windows Service...");

	try
	{
		using var stopProcess = Process.Start("sc", "stop TagDeployer");
		if (stopProcess != null)
		{
			await stopProcess.WaitForExitAsync();
			await Task.Delay(2000);
		}

		using var deleteProcess = Process.Start("sc", "delete TagDeployer");
		if (deleteProcess != null)
		{
			await deleteProcess.WaitForExitAsync();
		}

		Console.WriteLine("✅ TagDeployer service uninstalled");
	}
	catch (Exception ex)
	{
		Console.WriteLine($"❌ Service uninstall failed: {ex.Message}");
	}
}
