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
        command.AddCommand(CreateLogoutCommand(authService));
        command.AddCommand(CreateStatusCommand(authService));

        return command;
    }

    private static Command CreateLoginCommand(AuthService authService)
    {
        var command = new Command("login", "Sign in to the Admin API");

        var useDeviceCodeOption = new Option<bool>(
            "--use-device-code",
            "Use device code flow instead of opening a browser");
        command.AddOption(useDeviceCodeOption);

        command.SetHandler(async (bool useDeviceCode) =>
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
                var record = await authService.LoginAsync(useDeviceCode);
                AnsiConsole.MarkupLine("[green]Successfully authenticated![/]");
                AnsiConsole.MarkupLine($"  Account:  [bold]{record.Username}[/]");
                AnsiConsole.MarkupLine($"  Tenant:   [bold]{record.TenantId}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Authentication failed:[/] {ex.Message}");
            }
        }, useDeviceCodeOption);

        return command;
    }

    private static Command CreateLogoutCommand(AuthService authService)
    {
        var command = new Command("logout", "Sign out and clear saved credentials");

        command.SetHandler(async () =>
        {
            await authService.LogoutAsync();
            AnsiConsole.MarkupLine("[green]Signed out successfully.[/]");
        });

        return command;
    }

    private static Command CreateStatusCommand(AuthService authService)
    {
        var command = new Command("status", "Show authentication status");

        command.SetHandler(async () =>
        {
            AnsiConsole.MarkupLine("[bold]Authentication Configuration[/]\n");

            var table = new Table();
            table.AddColumn("Setting");
            table.AddColumn("Value");

            table.AddRow("Tenant ID", authService.TenantId ?? "[red]not set[/]");
            table.AddRow("Client ID", authService.ClientId ?? "[red]not set[/]");
            table.AddRow("API Scope", authService.ApiScope ?? "[dim](default)[/]");
            table.AddRow("Configured", authService.IsConfigured() ? "[green]yes[/]" : "[red]no[/]");

            var record = await authService.LoadAuthenticationRecordAsync();
            if (record is not null)
            {
                table.AddRow("Signed in", "[green]yes[/]");
                table.AddRow("Account", record.Username);
            }
            else
            {
                table.AddRow("Signed in", "[red]no[/]");
            }

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
