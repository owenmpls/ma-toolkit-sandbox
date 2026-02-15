using System.Data;

namespace MaToolkit.Automation.Shared.Services;

public interface ISqlQueryClient
{
    Task<DataTable> ExecuteQueryAsync(string connectionEnvVar, string query);
}
