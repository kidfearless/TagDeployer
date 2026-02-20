using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;

var knownTags = new HashSet<string>();
// get special folder
var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var folder = Path.Join(specialFolder, "MadAI");
Directory.CreateDirectory(folder);
var deployPath = "E:\\Deployments";

// Ensure deployment path exists
Directory.CreateDirectory(deployPath);

await Cli.Wrap("gh")
.WithArguments($"clone kidfearless/MadAI")
.WithWorkingDirectory(folder)
.ExecuteAsync();

var madaiFolder = Path.Join(folder, "MadAI");

var existingTags = (await GetRepoTags(madaiFolder)).ToHashSet();

while (true)
{
	try
	{
		await Cli.Wrap("git")
		.WithArguments("fetch --tags --prune")
		.WithWorkingDirectory(madaiFolder)
		.ExecuteAsync();

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
		// Log the error and continue the loop
		await Console.Error.WriteLineAsync($"Error in deployment loop: {ex}");
		await Task.Delay(TimeSpan.FromMinutes(1));
	}
}

async Task ExecuteAsync(GitTag tag)
{
	var safeTag = $"{tag.Tag.Replace('.', '_')}";
	var tagPath = Path.Combine(deployPath, safeTag);
	var git = Cli.Wrap("git").WithWorkingDirectory(deployPath);
	var gh = Cli.Wrap("gh").WithWorkingDirectory(deployPath);

	// clone tag
	await Cli.Wrap("gh").WithArguments($"clone kidfearless/MadAI \"{tagPath}\"").ExecuteAsync();
	await git.WithArguments($"checkout {tag}").ExecuteAsync();

	var projectPath = Path.Combine(tagPath, "Sitev2/MadAI.API");
	var dotnet = Cli.Wrap("dotnet").WithWorkingDirectory(projectPath);
	// publish api
	await dotnet.WithArguments("restore").ExecuteAsync();
	await dotnet.WithArguments("publish").ExecuteAsync();

	// move to deployment
	var publishOutput = Path.Combine(projectPath, "bin", "Release", "net10.0", "publish");
	var apiFolder = Path.Combine(tagPath, "Api", safeTag);
	Directory.Move(publishOutput, apiFolder);

	// publish
	var reactPath = Path.Combine(tagPath, "Sitev2", "MadAI.React");
	await Cli.Wrap("npm").WithArguments("install").WithWorkingDirectory(reactPath).ExecuteAsync();
	await Cli.Wrap("npm").WithArguments("run build").WithWorkingDirectory(reactPath).ExecuteAsync();

	var reactDist = Path.Combine(reactPath, "dist");
	var webHtml = Path.Combine(tagPath, "web", "html");
	Directory.Move(reactDist, webHtml);

	var extensionPath = Path.Combine(tagPath, "Extension");
	await Cli.Wrap("npm").WithArguments("install").WithWorkingDirectory(extensionPath).ExecuteAsync();
	// Build the extension for production with zip
	await Cli.Wrap("npm").WithArguments("run build:zip").WithWorkingDirectory(extensionPath).ExecuteAsync();

	// Path to the generated zip file
	var extensionZipFile = Path.Combine(extensionPath, "build", "chrome-mv3-prod.zip");
	var extensionBuildDir = Path.Combine(extensionPath, "build", "chrome-mv3-prod");

	// Move the zipped extension to the React app's public directory (for build integration)
	var extensionZipDest = Path.Combine(reactPath, "public", "mad-ai-extension.zip");
	File.Move(extensionZipFile, extensionZipDest);

	// Create deployment directory for versioned copy
	var deploymentsPath = Path.Combine(tagPath, "deployments");
	var versionedExtensionZip = Path.Combine(deploymentsPath, $"mad-ai-extension-{safeTag}.zip");
	File.Copy(extensionZipDest, versionedExtensionZip);

	// Also copy to web directory for web access
	var webDeploymentsPath = Path.Combine(tagPath, "web", "extensions");
	var webExtensionZip = Path.Combine(webDeploymentsPath, $"mad-ai-extension-{safeTag}.zip");
	File.Copy(extensionZipDest, webExtensionZip);

	// Move extension build to web directory
	var webExtPath = Path.Combine(tagPath, "web", $"ext-{safeTag}");
	Directory.Move(extensionBuildDir, webExtPath);

	await KillProcess("MadAI.API");

	var apiExePath = Path.Combine(apiFolder, "MadAI.API");
	_ = Cli.Wrap(apiExePath).WithWorkingDirectory(apiFolder).ExecuteAsync();

	await KillProcess("caddy");

	_ = Cli.Wrap("caddy").WithArguments("run --config Caddyfile").ExecuteAsync();
}

static async Task<IOrderedEnumerable<GitTag>> GetRepoTags(string madaiFolder)
{
	var tagsRaw = await Cli.Wrap("git")
	.WithArguments("""log --tags --simplify-by-decoration --pretty="%ai %d" """)
	.WithWorkingDirectory(madaiFolder)
	.ExecuteBufferedAsync();

	var matches = Regex.Matches(tagsRaw.StandardOutput, @"^([\d\- :+]+)\s+.*tag:\s*([^,\s]+)", RegexOptions.Multiline)
		.Select(m => new GitTag(DateTimeOffset.Parse(m.Groups[1].Value), m.Groups[2].Value))
		.Where(t => !string.IsNullOrEmpty(t.Tag) && t.TagDate != default)
		.OrderByDescending(t => t.TagDate);
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
