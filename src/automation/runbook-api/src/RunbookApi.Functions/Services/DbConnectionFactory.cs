using System.Data;
using Microsoft.Extensions.Options;
using RunbookApi.Functions.Settings;
using SharedDbConnectionFactory = MaToolkit.Automation.Shared.Services.DbConnectionFactory;
using IDbConnectionFactory = MaToolkit.Automation.Shared.Services.IDbConnectionFactory;

namespace RunbookApi.Functions.Services;

/// <summary>
/// Wrapper around the shared DbConnectionFactory that adapts IOptions&lt;RunbookApiSettings&gt;
/// to the connection string parameter expected by the shared implementation.
/// </summary>
public class RunbookApiDbConnectionFactory : IDbConnectionFactory
{
    private readonly SharedDbConnectionFactory _inner;

    public RunbookApiDbConnectionFactory(IOptions<RunbookApiSettings> settings)
    {
        _inner = new SharedDbConnectionFactory(settings.Value.SqlConnectionString);
    }

    public IDbConnection CreateConnection()
    {
        return _inner.CreateConnection();
    }
}
