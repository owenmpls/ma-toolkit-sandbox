using System.CommandLine;

namespace AdminCli.Commands;

public static class CommandHelpers
{
    /// <summary>
    /// Gets the global --api-url option by walking up the command hierarchy,
    /// falling back to creating a new one if not found.
    /// </summary>
    public static Option<string?> GetApiUrlOption(Command command)
    {
        return command.Parents
            .OfType<Command>()
            .SelectMany(c => c.Options)
            .OfType<Option<string?>>()
            .FirstOrDefault(o => o.Aliases.Contains("--api-url"))
            ?? new Option<string?>("--api-url");
    }
}
