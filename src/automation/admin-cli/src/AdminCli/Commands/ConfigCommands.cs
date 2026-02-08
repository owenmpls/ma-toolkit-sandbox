using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace AdminCli.Commands;

public static class ConfigCommands
{
    public static Command Create(IConfiguration configuration)
    {
        var command = new Command("config", "Manage CLI configuration");

        command.AddCommand(CreateShowCommand(configuration));
        command.AddCommand(CreateSetCommand());
        command.AddCommand(CreatePathCommand());

        return command;
    }

    private static Command CreateShowCommand(IConfiguration configuration)
    {
        var command = new Command("show", "Show current configuration");

        command.SetHandler(() =>
        {
            var configPath = Program.GetConfigPath();
            var apiUrl = configuration["API_URL"]
                ?? Environment.GetEnvironmentVariable("MATOOLKIT_API_URL");

            AnsiConsole.MarkupLine("[bold]Current Configuration[/]\n");

            var table = new Table();
            table.AddColumn("Setting");
            table.AddColumn("Value");
            table.AddColumn("Source");

            // API URL
            if (!string.IsNullOrEmpty(apiUrl))
            {
                var source = Environment.GetEnvironmentVariable("MATOOLKIT_API_URL") != null
                    ? "environment (MATOOLKIT_API_URL)"
                    : "config file";
                table.AddRow("API URL", apiUrl, source);
            }
            else
            {
                table.AddRow("API URL", "[red]not set[/]", "-");
            }

            // Auth settings
            var tenantId = configuration["TENANT_ID"]
                ?? Environment.GetEnvironmentVariable("MATOOLKIT_TENANT_ID");
            var clientId = configuration["CLIENT_ID"]
                ?? Environment.GetEnvironmentVariable("MATOOLKIT_CLIENT_ID");
            var apiScope = configuration["API_SCOPE"]
                ?? Environment.GetEnvironmentVariable("MATOOLKIT_API_SCOPE");

            table.AddRow("Tenant ID", tenantId ?? "[dim]not set[/]",
                !string.IsNullOrEmpty(tenantId) ? (Environment.GetEnvironmentVariable("MATOOLKIT_TENANT_ID") != null ? "environment" : "config file") : "-");
            table.AddRow("Client ID", clientId ?? "[dim]not set[/]",
                !string.IsNullOrEmpty(clientId) ? (Environment.GetEnvironmentVariable("MATOOLKIT_CLIENT_ID") != null ? "environment" : "config file") : "-");
            table.AddRow("API Scope", apiScope ?? "[dim](default)[/]",
                !string.IsNullOrEmpty(apiScope) ? (Environment.GetEnvironmentVariable("MATOOLKIT_API_SCOPE") != null ? "environment" : "config file") : "-");

            // Config file path
            table.AddRow("Config file", configPath, File.Exists(configPath) ? "[green]exists[/]" : "[dim]not found[/]");

            AnsiConsole.Write(table);

            if (string.IsNullOrEmpty(apiUrl))
            {
                AnsiConsole.MarkupLine("\n[yellow]Warning:[/] API URL not configured.");
                AnsiConsole.MarkupLine("Set it with: [dim]matoolkit config set api-url <url>[/]");
                AnsiConsole.MarkupLine("Or set environment variable: [dim]export MATOOLKIT_API_URL=<url>[/]");
            }
        });

        return command;
    }

    private static Command CreateSetCommand()
    {
        var keyArg = new Argument<string>("key", "Configuration key (api-url)");
        var valueArg = new Argument<string>("value", "Configuration value");

        var command = new Command("set", "Set a configuration value")
        {
            keyArg,
            valueArg
        };

        command.SetHandler(async (string key, string value) =>
        {
            var configPath = Program.GetConfigPath();
            var configDir = Path.GetDirectoryName(configPath)!;

            // Ensure config directory exists
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            // Load existing config or create new
            Dictionary<string, string> config;
            if (File.Exists(configPath))
            {
                var json = await File.ReadAllTextAsync(configPath);
                config = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            else
            {
                config = new Dictionary<string, string>();
            }

            // Map user-friendly keys to config keys
            var configKey = key.ToLowerInvariant() switch
            {
                "api-url" or "apiurl" or "url" => "API_URL",
                "tenant-id" or "tenantid" => "TENANT_ID",
                "client-id" or "clientid" => "CLIENT_ID",
                "api-scope" or "apiscope" or "scope" => "API_SCOPE",
                _ => key.ToUpperInvariant().Replace("-", "_")
            };

            config[configKey] = value;

            // Save config
            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, options));

            // Set restrictive file permissions on Unix/macOS
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            AnsiConsole.MarkupLine($"[green]Set[/] {key} = {value}");
            AnsiConsole.MarkupLine($"[dim]Saved to {configPath}[/]");
        }, keyArg, valueArg);

        return command;
    }

    private static Command CreatePathCommand()
    {
        var command = new Command("path", "Show configuration file path");

        command.SetHandler(() =>
        {
            var configPath = Program.GetConfigPath();
            Console.WriteLine(configPath);
        });

        return command;
    }
}
