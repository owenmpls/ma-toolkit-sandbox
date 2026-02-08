namespace MaToolkit.Automation.Shared.Exceptions;

public class TemplateResolutionException : Exception
{
    public string Template { get; }
    public IReadOnlyList<string> UnresolvedVariables { get; }

    public TemplateResolutionException(string template, IReadOnlyList<string> unresolvedVariables)
        : base($"Unresolved template variables [{string.Join(", ", unresolvedVariables)}] in template: {template}")
    {
        Template = template;
        UnresolvedVariables = unresolvedVariables;
    }
}
