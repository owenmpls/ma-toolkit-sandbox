using System.Text.RegularExpressions;
using MaToolkit.Automation.Shared.Exceptions;
using Microsoft.Extensions.Logging;

namespace MaToolkit.Automation.Shared.Services;

public class TemplateResolver : ITemplateResolver
{
    private static readonly Regex TemplatePattern = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);
    private readonly ILogger<TemplateResolver> _logger;

    public TemplateResolver(ILogger<TemplateResolver> logger)
    {
        _logger = logger;
    }

    public Dictionary<string, string> ResolveParams(
        Dictionary<string, string> paramTemplates,
        Dictionary<string, string> memberData,
        int batchId,
        DateTime? batchStartTime)
    {
        var resolved = new Dictionary<string, string>();

        foreach (var (key, template) in paramTemplates)
        {
            resolved[key] = ResolveString(template, memberData, batchId, batchStartTime);
        }

        return resolved;
    }

    public string ResolveString(string template, Dictionary<string, string> memberData,
        int batchId, DateTime? batchStartTime)
    {
        var unresolvedVariables = new List<string>();

        var result = TemplatePattern.Replace(template, match =>
        {
            var variableName = match.Groups[1].Value;

            // Special variables
            if (variableName == "_batch_id")
                return batchId.ToString();
            if (variableName == "_batch_start_time")
                return (batchStartTime ?? DateTime.UtcNow).ToString("o");

            // Key lookup
            if (memberData.TryGetValue(variableName, out var value))
                return value ?? string.Empty;

            // Also check with underscore prefix for system columns
            var altName = $"_{variableName}";
            if (memberData.TryGetValue(altName, out var altValue))
                return altValue ?? string.Empty;

            unresolvedVariables.Add(variableName);
            return match.Value;
        });

        if (unresolvedVariables.Count > 0)
            throw new TemplateResolutionException(template, unresolvedVariables);

        return result;
    }

    public Dictionary<string, string> ResolveInitParams(
        Dictionary<string, string> paramTemplates,
        int batchId,
        DateTime? batchStartTime)
    {
        var resolved = new Dictionary<string, string>();

        foreach (var (key, template) in paramTemplates)
        {
            var unresolvedVariables = new List<string>();

            resolved[key] = TemplatePattern.Replace(template, match =>
            {
                var variableName = match.Groups[1].Value;

                if (variableName == "_batch_id")
                    return batchId.ToString();
                if (variableName == "_batch_start_time")
                    return (batchStartTime ?? DateTime.UtcNow).ToString("o");

                unresolvedVariables.Add(variableName);
                return match.Value;
            });

            if (unresolvedVariables.Count > 0)
                throw new TemplateResolutionException(template, unresolvedVariables);
        }

        return resolved;
    }
}
