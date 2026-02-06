using System.Data;
using Microsoft.Extensions.Options;
using AdminApi.Functions.Settings;
using SharedDbConnectionFactory = MaToolkit.Automation.Shared.Services.DbConnectionFactory;
using IDbConnectionFactory = MaToolkit.Automation.Shared.Services.IDbConnectionFactory;

namespace AdminApi.Functions.Services;

/// <summary>
/// Wrapper around the shared DbConnectionFactory that adapts IOptions&lt;AdminApiSettings&gt;
/// to the connection string parameter expected by the shared implementation.
/// </summary>
public class AdminApiDbConnectionFactory : IDbConnectionFactory
{
    private readonly SharedDbConnectionFactory _inner;

    public AdminApiDbConnectionFactory(IOptions<AdminApiSettings> settings)
    {
        _inner = new SharedDbConnectionFactory(settings.Value.SqlConnectionString);
    }

    public IDbConnection CreateConnection()
    {
        return _inner.CreateConnection();
    }
}
