using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class DeployerService : BackgroundService
{
	private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

	private static string GetLogFilePath()
	{
		Directory.CreateDirectory(LogDirectory);
		return Path.Combine(LogDirectory, $"TagDeployer-{DateTime.Now:yyyy-MM-dd}.log");
	}

	public static void Log(string message)
	{
		var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
		try
		{
			File.AppendAllText(GetLogFilePath(), line + Environment.NewLine);
		}
		catch { }
		Console.WriteLine(line);
	}

	public static void LogError(string message, Exception? ex = null)
	{
		var line = ex != null
		? $"{message}: {ex}"
		 : message;
		Log($"ERROR: {line}");
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		Log($"Service starting. Log directory: {LogDirectory}");
		Log($"Running as user: {Environment.UserName}");
		Log($"BaseDirectory: {AppContext.BaseDirectory}");

		try
		{
			var deploymentPath = GetDeploymentPaths();
			Log($"Deployment path: {deploymentPath.Path}");

			var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			Log($"MyDocuments resolved to: '{specialFolder}'");
			if (string.IsNullOrEmpty(specialFolder))
			{
				// Fallback when running as SYSTEM — MyDocuments is empty
				specialFolder = Path.Combine(AppContext.BaseDirectory, "data");
				Log($"MyDocuments was empty, falling back to: {specialFolder}");
			}

			var folder = Path.Join(specialFolder, "MadAI");
			Directory.CreateDirectory(folder);

			var madaiFolder = Path.Join(folder, "MadAI");
			if (Directory.Exists(madaiFolder) && Directory.Exists(Path.Combine(madaiFolder, ".git")))
			{
				Log($"Repository already exists at {madaiFolder}, pulling latest...");
				await Cli.Wrap("git").WithArguments("pull").WithWorkingDirectory(madaiFolder).RunAsync();
			}
			else
			{
				Log("Cloning repository...");
				await Cli.Wrap("gh").WithArguments("repo clone kidfearless/MadAI").WithWorkingDirectory(folder).RunAsync();

				if (!Directory.Exists(madaiFolder))
				{
					LogError($"Clone failed — directory '{madaiFolder}' was not created. " +
							"This is likely because 'gh' is not authenticated for the service account. " +
							"Either run the service under your user account (services.msc → Log On tab), " +
							"or run 'gh auth login' as the service account.");
					throw new InvalidOperationException($"Repository clone failed — '{madaiFolder}' does not exist.");
				}
			}

			var existingTags = (await GetRepoTags(madaiFolder)).ToHashSet();
			Log($"Found {existingTags.Count} tags");

			if (existingTags.Count == 0)
			{
				LogError("No tags found in repository — cannot deploy. Service will retry in polling loop.");
			}
			else
			{
				// On startup, kill any existing instances and deploy the latest tag
				await KillProcess("MadAI.API");
				await KillProcess("caddy");
				var latestTag = existingTags.OrderByDescending(t => t.TagDate).First();
				Log($"Startup: deploying latest tag {latestTag.Tag}");
				await DeployTagAsync(latestTag, deploymentPath);
			}

			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await Cli.Wrap("git").WithArguments("fetch --tags --prune").WithWorkingDirectory(madaiFolder).RunAsync();

					var tags = (await GetRepoTags(madaiFolder)).ToList();
					if (tags.Count == 0)
					{
						Log("Warning: no tags found during poll.");
						await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
						continue;
					}
					var newestTag = tags.First();
					var contains = existingTags.Contains(newestTag);
					existingTags = [.. tags];
					if (!contains)
					{
						Log($"New tag detected: {newestTag.Tag}, deploying...");
						await DeployTagAsync(newestTag, deploymentPath);
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					LogError("Error in deployment loop", ex);
				}
				await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			LogError("Fatal error in ExecuteAsync — service will stop", ex);
			throw; // re-throw so the host knows the service failed
		}
	}

	public static async Task DeployTagAsync(GitTag tag, DeploymentPath deploymentPath)
	{
		var deployPath = deploymentPath.Path;
		var caddyFile = deploymentPath.CaddyFile;
		var caddyExe = deploymentPath.CaddyExe;

		var safeTag = $"{tag.Tag.Replace('.', '_')}";
		var tagPath = Path.Combine(deployPath, safeTag);

		// clone tag
		await Cli.Wrap("gh").WithArguments($"repo clone kidfearless/MadAI \"{tagPath}\"").RunAsync();
		var git = Cli.Wrap("git").WithWorkingDirectory(tagPath);
		await git.WithArguments($"checkout tags/{tag.Tag}").RunAsync();

		var projectPath = Path.Combine(tagPath, "Sitev2", "MadAI.API");

		// build rust with shared target dir so dependencies are cached across deployments
		var cargoTargetDir = Path.Combine(deployPath, "cargo-cache");
		var rustPath = Path.Combine(tagPath, "PdfInterop");
		await Cli.Wrap("cargo")
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
		await dotnet.WithArguments("publish /p:SkipRustBuild=true").RunAsync();

		// handle frontend and extension builds
		var reactPath = Path.Combine(tagPath, "Sitev2", "MadAI.React");
		var npm = Cli.Wrap("npm");
		await npm.WithArguments("install").WithWorkingDirectory(reactPath).RunAsync();
		await npm.WithArguments("run build").WithWorkingDirectory(reactPath).RunAsync();

		// copy react dist to wwwroot where caddy expects it
		var distPath = Path.Combine(reactPath, "dist");
		var wwwroot = Path.Combine(tagPath, "wwwroot");
		if (Directory.Exists(distPath))
		{
			CopyDirectory(distPath, wwwroot);
		}

		var extensionPath = Path.Combine(tagPath, "Extension");
		await npm.WithArguments("install").WithWorkingDirectory(extensionPath).RunAsync();
		await npm.WithArguments("run build:zip").WithWorkingDirectory(extensionPath).RunAsync();

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

	public static async Task<HashSet<GitTag>> GetRepoTags(string madaiFolder)
	{
		var tagsRaw = await Cli.Wrap("git")
			.WithArguments("""log --tags --simplify-by-decoration --pretty="%ai %d" """)
			.WithWorkingDirectory(madaiFolder)
			.ExecuteBufferedAsync();
		var matches = Regex.Matches(tagsRaw.StandardOutput, @"^([\d\- :+]+)\s+.*tag:\s*([^,\s)]+)", RegexOptions.Multiline)
			.Select(m => new GitTag(DateTimeOffset.Parse(m.Groups[1].Value), m.Groups[2].Value))
			.Where(t => !string.IsNullOrEmpty(t.Tag) && t.TagDate != default)
			.OrderByDescending(t => t.TagDate)
			.ToHashSet();

		return matches;
	}

	public static async Task KillProcess(string name)
	{
		await Cli.Wrap("taskkill")
			.WithArguments($"/F /T /FI \"IMAGENAME eq {name}*\"")
			.WithValidation(CommandResultValidation.None)
			.ExecuteBufferedAsync();
	}

	public static void CopyDirectory(string source, string destination)
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

	public static DeploymentPath GetDeploymentPaths()
	{
		var root = Path.Combine("D:", "Deployments");
		var caddyFile = Path.Combine(root, "CaddyFile");
		var caddy = Path.Combine(root, "caddy.exe");

		return new(root, caddyFile, caddy);
	}
}
