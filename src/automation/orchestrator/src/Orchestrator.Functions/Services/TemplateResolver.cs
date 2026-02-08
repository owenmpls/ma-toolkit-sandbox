using System.Data;
using System.Text.RegularExpressions;
using MaToolkit.Automation.Shared.Exceptions;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Functions.Services;

public interface ITemplateResolver
{
    Dictionary<string, string> ResolveParams(
        Dictionary<string, string> paramTemplates,
        DataRow memberData,
        int batchId,
        DateTime? batchStartTime);

    string ResolveString(string template, DataRow memberData, int batchId, DateTime? batchStartTime);

    /// <summary>
    /// Resolve parameters for init steps (no member data available).
    /// </summary>
    Dictionary<string, string> ResolveInitParams(
        Dictionary<string, string> paramTemplates,
        int batchId,
        DateTime? batchStartTime);
}

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
        DataRow memberData,
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

    public string ResolveString(string template, DataRow memberData, int batchId, DateTime? batchStartTime)
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

            // Column lookup
            if (memberData.Table.Columns.Contains(variableName))
            {
                var value = memberData[variableName];
                if (value is DBNull || value is null)
                    return string.Empty;
                return value.ToString()!;
            }

            // Also check without system prefix for columns stored with underscore prefix
            var altName = $"_{variableName}";
            if (memberData.Table.Columns.Contains(altName))
            {
                var value = memberData[altName];
                if (value is DBNull || value is null)
                    return string.Empty;
                return value.ToString()!;
            }

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
            resolved[key] = TemplatePattern.Replace(template, match =>
            {
                var variableName = match.Groups[1].Value;

                if (variableName == "_batch_id")
                    return batchId.ToString();
                if (variableName == "_batch_start_time")
                    return (batchStartTime ?? DateTime.UtcNow).ToString("o");

                _logger.LogWarning("Unresolved init template variable: {Variable}", variableName);
                return match.Value;
            });
        }

        return resolved;
    }
}
