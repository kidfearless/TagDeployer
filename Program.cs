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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToList();

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

	var allTags = await DeployerService.GetRepoTags(madaiFolderDeploy);
	var targetTag = allTags.FirstOrDefault(t => t.Tag == targetTagName);

	var deploymentPath = DeployerService.GetDeploymentPaths();
	await DeployerService.DeployTagAsync(targetTag, deploymentPath);
	await Task.Delay(-1);
	return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "TagDeployer");
builder.Services.AddHostedService<DeployerService>();
var host = builder.Build();
await host.RunAsync();

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

static class E
{
	extension(Command task)
	{
		public async Task<BufferedCommandResult> ExecuteBufferedAsync()
		{
			DeployerService.Log($"> {task.TargetFilePath} {task.Arguments}");
			var result = await BufferedCommandExtensions.ExecuteBufferedAsync(task);
			DeployerService.Log(result.StandardOutput);
			DeployerService.LogError(result.StandardError);
			return result;
		}
		public async Task<CommandResult> RunAsync()
		{
			DeployerService.Log($"> {task.TargetFilePath} {task.Arguments}");
			var result = await task
				.WithValidation(CommandResultValidation.None)
				.WithStandardOutputPipe(
					PipeTarget.ToDelegate(l => DeployerService.Log(l)))
				.WithStandardErrorPipe(
					PipeTarget.ToDelegate(line => DeployerService.LogError(line)))
			.ExecuteAsync(default);

			return result;
		}
	}
}
