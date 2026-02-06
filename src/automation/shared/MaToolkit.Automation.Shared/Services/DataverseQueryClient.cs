using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MaToolkit.Automation.Shared.Services;

public class DataverseQueryClient : IDataverseQueryClient
{
    private readonly IConfiguration _config;
    private readonly ILogger<DataverseQueryClient> _logger;

    public DataverseQueryClient(IConfiguration config, ILogger<DataverseQueryClient> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<DataTable> ExecuteQueryAsync(string connectionEnvVar, string query)
    {
        var connectionString = _config[connectionEnvVar]
            ?? throw new InvalidOperationException($"Connection string '{connectionEnvVar}' not found in configuration");

        _logger.LogInformation("Executing Dataverse query via TDS endpoint");

        var dataTable = new DataTable();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync();
        dataTable.Load(reader);

        _logger.LogInformation("Dataverse query returned {RowCount} rows", dataTable.Rows.Count);
        return dataTable;
    }
}
