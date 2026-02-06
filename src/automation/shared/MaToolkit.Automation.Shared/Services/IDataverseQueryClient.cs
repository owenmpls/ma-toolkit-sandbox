using System.Data;

namespace MaToolkit.Automation.Shared.Services;

public interface IDataverseQueryClient
{
    Task<DataTable> ExecuteQueryAsync(string connectionEnvVar, string query);
}
