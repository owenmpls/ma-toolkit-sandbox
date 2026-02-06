using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using AdminCli.Commands;
using AdminCli.Services;
using Microsoft.Extensions.Configuration;

namespace AdminCli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configuration = BuildConfiguration();
        var apiClient = new AdminApiClient(configuration);

        var rootCommand = new RootCommand("M&A Toolkit CLI - Manage migration automation runbooks and batches")
        {
            Name = "matoolkit"
        };

        // Add global options
        var apiUrlOption = new Option<string?>(
            aliases: new[] { "--api-url", "-u" },
            description: "Admin API base URL (or set MATOOLKIT_API_URL environment variable)");

        rootCommand.AddGlobalOption(apiUrlOption);

        // Add command groups
        rootCommand.AddCommand(RunbookCommands.Create(apiClient));
        rootCommand.AddCommand(AutomationCommands.Create(apiClient));
        rootCommand.AddCommand(QueryCommands.Create(apiClient));
        rootCommand.AddCommand(TemplateCommands.Create(apiClient));
        rootCommand.AddCommand(BatchCommands.Create(apiClient));
        rootCommand.AddCommand(ConfigCommands.Create(configuration));

        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseExceptionHandler((ex, context) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();

                if (Environment.GetEnvironmentVariable("MATOOLKIT_DEBUG") == "1")
                {
                    Console.Error.WriteLine(ex.StackTrace);
                }

                context.ExitCode = 1;
            })
            .Build();

        return await parser.InvokeAsync(args);
    }

    private static IConfiguration BuildConfiguration()
    {
        var configPath = GetConfigPath();

        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables("MATOOLKIT_");

        if (File.Exists(configPath))
        {
            builder.AddJsonFile(configPath, optional: true);
        }

        return builder.Build();
    }

    public static string GetConfigPath()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".matoolkit", "config.json");
    }
}
