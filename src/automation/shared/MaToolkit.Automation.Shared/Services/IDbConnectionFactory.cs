using System.Data;

namespace MaToolkit.Automation.Shared.Services;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
