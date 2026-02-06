using System.CommandLine;
using AdminCli.Services;
using Spectre.Console;

namespace AdminCli.Commands;

public static class QueryCommands
{
    public static Command Create(AdminApiClient apiClient)
    {
        var command = new Command("query", "Preview runbook data source queries");

        command.AddCommand(CreatePreviewCommand(apiClient));

        return command;
    }

    private static Command CreatePreviewCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("runbook", "Runbook name");
        var limitOption = new Option<int>(
            aliases: new[] { "--limit", "-l" },
            getDefaultValue: () => 10,
            description: "Number of sample rows to display");
        var jsonOption = new Option<bool>(
            aliases: new[] { "--json", "-j" },
            description: "Output full results as JSON");

        var command = new Command("preview", "Execute query and preview results (no batch created)")
        {
            nameArg,
            limitOption,
            jsonOption
        };

        command.SetHandler(async (string name, int limit, bool json, string? apiUrl) =>
        {
            var result = await AnsiConsole.Status()
                .StartAsync($"Executing query for [blue]{name}[/]...", async ctx =>
                {
                    return await apiClient.PreviewQueryAsync(name, apiUrl);
                });

            if (json)
            {
                var jsonOutput = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                Console.WriteLine(jsonOutput);
                return;
            }

            // Summary
            AnsiConsole.MarkupLine($"\n[bold]Query Results[/]");
            AnsiConsole.MarkupLine($"Total rows: [blue]{result.RowCount}[/]");
            AnsiConsole.MarkupLine($"Columns: [dim]{string.Join(", ", result.Columns)}[/]");

            // Batch groups
            if (result.BatchGroups.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[bold]Batch Groups ({result.BatchGroups.Count})[/]");
                var batchTable = new Table();
                batchTable.AddColumn("Batch Time");
                batchTable.AddColumn("Members");

                foreach (var group in result.BatchGroups.Take(20))
                {
                    batchTable.AddRow(group.BatchTime, group.MemberCount.ToString());
                }

                if (result.BatchGroups.Count > 20)
                {
                    batchTable.AddRow("[dim]...[/]", $"[dim]+{result.BatchGroups.Count - 20} more[/]");
                }

                AnsiConsole.Write(batchTable);
            }

            // Sample data
            if (result.Sample.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[bold]Sample Data[/] (first {Math.Min(limit, result.Sample.Count)} rows)");

                var sampleTable = new Table();
                foreach (var col in result.Columns)
                {
                    sampleTable.AddColumn(col);
                }

                foreach (var row in result.Sample.Take(limit))
                {
                    var values = result.Columns.Select(col =>
                    {
                        if (row.TryGetValue(col, out var value) && value != null)
                        {
                            var str = value.ToString() ?? "";
                            return str.Length > 40 ? str.Substring(0, 37) + "..." : str;
                        }
                        return "[dim]null[/]";
                    }).ToArray();
                    sampleTable.AddRow(values);
                }

                AnsiConsole.Write(sampleTable);
            }
        }, nameArg, limitOption, jsonOption, GetApiUrlOption(command));

        return command;
    }

    private static Option<string?> GetApiUrlOption(Command command)
    {
        return command.Parents
            .OfType<Command>()
            .SelectMany(c => c.Options)
            .OfType<Option<string?>>()
            .FirstOrDefault(o => o.Aliases.Contains("--api-url"))
            ?? new Option<string?>("--api-url");
    }
}
