TagDeployer/README.md
Create comprehensive README.md
```

# TagDeployer 🚀

An automated deployment system that continuously monitors Git repositories for new tags and automatically builds and deploys your applications.

## Features ✨

- **Automated Tag Monitoring**: Watches your Git repository for new tags
- **Multi-Platform Support**: Works on both Windows and Linux
- **Full Stack Deployment**: Builds and deploys both API and React applications
- **Extension Building**: Automatically packages and deploys browser extensions
- **Process Management**: Gracefully handles service restarts
- **Error Handling**: Robust error handling with automatic retry logic

## Prerequisites 📋

Before running TagDeployer, ensure you have the following installed on your system:

### Windows
- [Git CLI](https://git-scm.com/downloads)
- [.NET 8.0+](https://dotnet.microsoft.com/download)
- [Node.js 18.0+](https://nodejs.org/)
- [GitHub CLI](https://cli.github.com/)
- Caddy (optional, for web serving)

### Linux
- Git CLI
- .NET 8.0+
- Node.js 18.0+
- GitHub CLI
- Caddy (optional, for web serving)

## Installation 🔧

1. Clone this repository:
```bash
git clone https://github.com/kidfearless/TagDeployer.git
cd TagDeployer
```

2. Install dependencies:
```bash
cd TagDeployer
dotnet restore
```

## Usage 📖

### Basic Usage

Configure your deployment path in the `Program.cs` file (defaults to `E:\Deployments` on Windows):

```csharp
var deployPath = "E:\\Deployments"; // Windows
// or
var deployPath = "/home/user/deployments"; // Linux
```

Run the deployer:
```bash
dotnet run
```

### Configuration

The system will:
1. Clone the `kidfearless/MadAI` repository to your Documents folder
2. Monitor for new Git tags every minute
3. When a new tag is found:
   - Clone the specific tag to your deployment folder
   - Build the API using `dotnet publish`
   - Build the React frontend using `npm run build`
   - Package the browser extension
   - Restart the API process
   - Restart Caddy (if running)

## Project Structure 📁

```
TagDeployer/
├── Program.cs          # Main deployment logic
├── GitTag.cs         # Git tag data model
├── Caddyfile         # Web server configuration
├── TagDeployer.csproj
├── .gitignore
└── README.md
```

## Build Process 🏗️

When a new tag is detected, TagDeployer performs the following:

1. **Clone Tag**: Creates a new folder based on the tag name
2. **Build API**: Runs `dotnet restore` and `dotnet publish` on `Sitev2/MadAI.API`
3. **Build Frontend**: Runs `npm install` and `npm run build` on `Sitev2/MadAI.React`
4. **Build Extension**: Runs `npm install` and `npm run build:zip` on `Extension`
5. **Deploy**: Moves built files to appropriate locations
6. **Restart Services**: Stops the current API process and Caddy, then restarts them

## Configuration Options ⚙️

### Deployment Path
The deployment directory where all built applications will be placed:
```csharp
var deployPath = "E:\\Deployments";
```

### Repository Monitoring
Currently configured to monitor:
```csharp
await Cli.Wrap("gh").WithArguments($"clone kidfearless/MadAI")
```

### Process Management
Adjust process kill/restart behavior by modifying:
```csharp
await KillProcess("MadAI.API");    // Kill API process
await KillProcess("caddy");           // Kill Caddy process
```

## Error Handling 🛡️

TagDeployer includes comprehensive error handling:

- **Try-Catch Loop**: The main polling loop is wrapped in try-catch to prevent crashes
- **Delay Mechanism**: 1-minute delay between iterations regardless of success/failure
- **Process Termination**: Force termination on Windows with `/F /T` flags for complete process tree cleanup
- **Error Logging**: Errors are logged to console output for debugging

## Customization 🔨

### Adding Additional Build Steps
Modify the `ExecuteAsync` method to include additional build steps:

```csharp
// Add custom build steps here
await CustomBuildStep(tagPath);
```

### Changing Repository
To monitor a different repository, update the clone command:

```csharp
await Cli.Wrap("gh").WithArguments($"clone username/repository")
.WithArguments($"clone your-username/your-repo")
```

### Adjusting Polling Frequency
Change the delay in the finally block:

```csharp
await Task.Delay(TimeSpan.FromMinutes(5)); // Check every 5 minutes
```

## Troubleshooting 🔧

### Common Issues

1. **GitHub CLI not authenticated**
   ```bash
   gh auth login
   ```

2. **Permission denied on process kill**
   - Ensure running with admin privileges on Windows
   - On Linux, ensure user has appropriate permissions

3. **Build failures**
   - Check that all prerequisites are installed
   - Verify network connectivity for package downloads
   - Check console output for specific build errors

### Logs
The deployment process logs progress to console. Check output for detailed error messages and warnings.

## License 📄

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Contributing 🤝

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## Acknowledgments 🙏

- Uses [CliWrap](https://github.com/Tyrrrz/CliWrap) for command-line interface handling
- Built for automated deployment workflows
- Inspired by continuous deployment practices