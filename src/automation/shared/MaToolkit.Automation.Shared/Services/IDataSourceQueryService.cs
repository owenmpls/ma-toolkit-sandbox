using System.Data;
using MaToolkit.Automation.Shared.Models.Yaml;

namespace MaToolkit.Automation.Shared.Services;

public interface IDataSourceQueryService
{
    Task<DataTable> ExecuteAsync(DataSourceConfig config);
}
