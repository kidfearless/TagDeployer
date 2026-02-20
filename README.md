# TagDeployer

Watches Git repositories for new tags and automatically deploys them. Built for the MadAI project.

## What it does

- Polls `kidfearless/MadAI` for new Git tags every minute
- Automatically clones, builds, and deploys new versions
- Builds: .NET API, React frontend, Chrome extension, then restarts services

## Dependencies

- .NET 8.0+
- Node.js 18.0+
- GitHub CLI (`gh`)

## Quick Start

```bash
cd TagDeployer
dotnet restore
dotnet run
```

The deployer runs continuously, checking for new tags every 60 seconds.

## Configuration

Edit the deployment path in `Program.cs`:

```csharp
var deployPath = "E:\\Deployments";  // Windows
var deployPath = "/home/user/deployments";  // Linux
```

## How it works

1. Watches local copy of `kidfearless/MadAI` repo
2. On new tag: clones tag-specific copy, builds everything, swaps in new version
3. Kills old APIs/services, starts new ones (MadAI.API, Caddy)

## For other projects

Change the repo to monitor by editing:

```csharp
await Cli.Wrap("gh").WithArguments($"clone your-org/your-repo")
```
