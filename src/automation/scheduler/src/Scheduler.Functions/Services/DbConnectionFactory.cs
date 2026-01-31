using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Scheduler.Functions.Settings;

namespace Scheduler.Functions.Services;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IOptions<SchedulerSettings> settings)
    {
        _connectionString = settings.Value.SqlConnectionString;
    }

    public IDbConnection CreateConnection()
    {
        return new SqlConnection(_connectionString);
    }
}
