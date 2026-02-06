using Microsoft.Extensions.Options;
using Orchestrator.Functions.Settings;
using MaToolkit.Automation.Shared.Services;

namespace Orchestrator.Functions.Services;

/// <summary>
/// Adapts IOptions&lt;OrchestratorSettings&gt; to create the shared DbConnectionFactory.
/// </summary>
public class OrchestratorDbConnectionFactory : DbConnectionFactory
{
    public OrchestratorDbConnectionFactory(IOptions<OrchestratorSettings> settings)
        : base(settings.Value.SqlConnectionString)
    {
    }
}
