using System.CommandLine;
using AdminCli.Services;
using Spectre.Console;

namespace AdminCli.Commands;

public static class AuthCommands
{
    public static Command Create(AuthService authService)
    {
        var command = new Command("auth", "Manage authentication");

        command.AddCommand(CreateLoginCommand(authService));
        command.AddCommand(CreateStatusCommand(authService));

        return command;
    }

    private static Command CreateLoginCommand(AuthService authService)
    {
        var command = new Command("login", "Sign in using device code flow");

        command.SetHandler(async () =>
        {
            if (!authService.IsConfigured())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Authentication not configured.");
                AnsiConsole.MarkupLine("Set tenant-id and client-id first:");
                AnsiConsole.MarkupLine("  [dim]matoolkit config set tenant-id <value>[/]");
                AnsiConsole.MarkupLine("  [dim]matoolkit config set client-id <value>[/]");
                return;
            }

            try
            {
                var token = await authService.GetAccessTokenAsync();
                AnsiConsole.MarkupLine("[green]Successfully authenticated![/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Authentication failed:[/] {ex.Message}");
            }
        });

        return command;
    }

    private static Command CreateStatusCommand(AuthService authService)
    {
        var command = new Command("status", "Show authentication status");

        command.SetHandler(() =>
        {
            AnsiConsole.MarkupLine("[bold]Authentication Configuration[/]\n");

            var table = new Table();
            table.AddColumn("Setting");
            table.AddColumn("Value");

            table.AddRow("Tenant ID", authService.TenantId ?? "[red]not set[/]");
            table.AddRow("Client ID", authService.ClientId ?? "[red]not set[/]");
            table.AddRow("API Scope", authService.ApiScope ?? "[dim](default)[/]");
            table.AddRow("Configured", authService.IsConfigured() ? "[green]yes[/]" : "[red]no[/]");

            AnsiConsole.Write(table);

            if (!authService.IsConfigured())
            {
                AnsiConsole.MarkupLine("\n[yellow]Warning:[/] Authentication not configured.");
                AnsiConsole.MarkupLine("Set values with:");
                AnsiConsole.MarkupLine("  [dim]matoolkit config set tenant-id <value>[/]");
                AnsiConsole.MarkupLine("  [dim]matoolkit config set client-id <value>[/]");
                AnsiConsole.MarkupLine("  [dim]matoolkit config set api-scope <value>[/] (optional)");
            }
        });

        return command;
    }
}
