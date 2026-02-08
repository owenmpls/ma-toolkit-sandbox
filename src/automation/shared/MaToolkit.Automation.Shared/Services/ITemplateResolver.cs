namespace MaToolkit.Automation.Shared.Services;

public interface ITemplateResolver
{
    Dictionary<string, string> ResolveParams(
        Dictionary<string, string> paramTemplates,
        Dictionary<string, string> memberData,
        int batchId,
        DateTime? batchStartTime);

    string ResolveString(string template, Dictionary<string, string> memberData,
        int batchId, DateTime? batchStartTime);

    /// <summary>
    /// Resolve parameters for init steps (no member data available).
    /// </summary>
    Dictionary<string, string> ResolveInitParams(
        Dictionary<string, string> paramTemplates,
        int batchId,
        DateTime? batchStartTime);
}
