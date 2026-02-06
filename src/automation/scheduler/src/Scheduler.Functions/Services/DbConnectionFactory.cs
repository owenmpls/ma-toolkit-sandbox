using System.Data;
using Microsoft.Extensions.Options;
using Scheduler.Functions.Settings;
using SharedDbConnectionFactory = MaToolkit.Automation.Shared.Services.DbConnectionFactory;

namespace Scheduler.Functions.Services;

/// <summary>
/// Wrapper around the shared DbConnectionFactory that adapts IOptions&lt;SchedulerSettings&gt;
/// to the shared library's constructor that takes a connection string directly.
/// </summary>
public class SchedulerDbConnectionFactory : MaToolkit.Automation.Shared.Services.IDbConnectionFactory
{
    private readonly SharedDbConnectionFactory _inner;

    public SchedulerDbConnectionFactory(IOptions<SchedulerSettings> settings)
    {
        _inner = new SharedDbConnectionFactory(settings.Value.SqlConnectionString);
    }

    public IDbConnection CreateConnection() => _inner.CreateConnection();
}
