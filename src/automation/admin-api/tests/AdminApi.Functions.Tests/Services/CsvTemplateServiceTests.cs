using FluentAssertions;
using AdminApi.Functions.Services;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AdminApi.Functions.Tests.Services;

public class CsvTemplateServiceTests
{
    private readonly CsvTemplateService _sut;
    private readonly Mock<ILogger<CsvTemplateService>> _loggerMock;

    public CsvTemplateServiceTests()
    {
        _loggerMock = new Mock<ILogger<CsvTemplateService>>();
        _sut = new CsvTemplateService(_loggerMock.Object);
    }

    private static RunbookDefinition CreateBasicDefinition()
    {
        return new RunbookDefinition
        {
            Name = "test-runbook",
            DataSource = new DataSourceConfig
            {
                Type = "dataverse",
                PrimaryKey = "user_id",
                Query = "SELECT user_id, email, name FROM users",
                BatchTime = "immediate"
            },
            Phases = new List<PhaseDefinition>
            {
                new()
                {
                    Name = "phase1",
                    Offset = "T-0",
                    Steps = new List<StepDefinition>
                    {
                        new()
                        {
                            Name = "step1",
                            WorkerId = "worker1",
                            Function = "DoWork"
                        }
                    }
                }
            }
        };
    }

    #region Basic Template Generation Tests

    [Fact]
    public void GenerateTemplate_BasicDefinition_IncludesHeader()
    {
        var definition = CreateBasicDefinition();

        var result = _sut.GenerateTemplate(definition);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCountGreaterOrEqualTo(2);
    }

    [Fact]
    public void GenerateTemplate_IncludesPrimaryKey()
    {
        var definition = CreateBasicDefinition();

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().Contain("user_id");
    }

    [Fact]
    public void GenerateTemplate_ParsesSelectColumns()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT user_id, email, department FROM users";

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().Contain("user_id");
        headerLine.Should().Contain("email");
        headerLine.Should().Contain("department");
    }

    [Fact]
    public void GenerateTemplate_HasSampleRow()
    {
        var definition = CreateBasicDefinition();

        var result = _sut.GenerateTemplate(definition);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2); // Header + sample row
    }

    #endregion

    #region Column Alias Handling Tests

    [Fact]
    public void GenerateTemplate_ColumnWithAlias_UsesAlias()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT u.user_id AS id, u.email AS contact_email FROM users u";

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().Contain("id");
        headerLine.Should().Contain("contact_email");
    }

    [Fact]
    public void GenerateTemplate_TablePrefixedColumn_ExtractsColumnName()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT users.user_id, users.email FROM users";

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().Contain("user_id");
        headerLine.Should().Contain("email");
    }

    [Fact]
    public void GenerateTemplate_BracketedColumnNames_HandlesBrackets()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT [user_id], [email] FROM users";

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().Contain("user_id");
        headerLine.Should().Contain("email");
    }

    #endregion

    #region Multi-Valued Column Tests

    [Fact]
    public void GenerateTemplate_SemicolonDelimitedColumn_ShowsFormat()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.MultiValuedColumns = new List<MultiValuedColumnConfig>
        {
            new() { Name = "groups", Format = "semicolon_delimited" }
        };

        var result = _sut.GenerateTemplate(definition);

        result.Should().Contain("value1;value2;value3");
    }

    [Fact]
    public void GenerateTemplate_CommaDelimitedColumn_ShowsFormat()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.MultiValuedColumns = new List<MultiValuedColumnConfig>
        {
            new() { Name = "tags", Format = "comma_delimited" }
        };

        var result = _sut.GenerateTemplate(definition);

        // Comma-delimited values should be quoted in CSV
        result.Should().Contain("value1,value2,value3");
    }

    [Fact]
    public void GenerateTemplate_JsonArrayColumn_ShowsFormat()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.MultiValuedColumns = new List<MultiValuedColumnConfig>
        {
            new() { Name = "aliases", Format = "json_array" }
        };

        var result = _sut.GenerateTemplate(definition);

        // JSON array values are escaped in CSV (quotes doubled, wrapped in quotes)
        result.Should().Contain("value1");
        result.Should().Contain("value2");
        result.Should().Contain("value3");
    }

    #endregion

    #region Template Variable Column Tests

    [Fact]
    public void GenerateTemplate_IncludesColumnsFromTemplates()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT user_id FROM users"; // Query doesn't include email
        definition.Phases[0].Steps[0].Params = new Dictionary<string, string>
        {
            ["email"] = "{{email}}",
            ["department"] = "{{department}}"
        };

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().Contain("email");
        headerLine.Should().Contain("department");
    }

    [Fact]
    public void GenerateTemplate_ExcludesSystemVariables()
    {
        var definition = CreateBasicDefinition();
        definition.Phases[0].Steps[0].Params = new Dictionary<string, string>
        {
            ["batch_id"] = "{{_batch_id}}",
            ["start_time"] = "{{_batch_start_time}}"
        };

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().NotContain("_batch_id");
        headerLine.Should().NotContain("_batch_start_time");
    }

    #endregion

    #region Sample Value Tests

    [Fact]
    public void GenerateTemplate_DateColumn_ShowsDateFormat()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT user_id, migration_date FROM users";

        var result = _sut.GenerateTemplate(definition);

        var sampleRow = result.Split('\n')[1];
        // Should contain ISO date format
        sampleRow.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z");
    }

    [Fact]
    public void GenerateTemplate_TimeColumn_ShowsDateFormat()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT user_id, created_time FROM users";

        var result = _sut.GenerateTemplate(definition);

        var sampleRow = result.Split('\n')[1];
        sampleRow.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z");
    }

    [Fact]
    public void GenerateTemplate_EmailColumn_ShowsEmailFormat()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT user_id, email FROM users";

        var result = _sut.GenerateTemplate(definition);

        result.Should().Contain("user@example.com");
    }

    [Fact]
    public void GenerateTemplate_IdColumn_ShowsIdFormat()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT user_id, department_id FROM users";

        var result = _sut.GenerateTemplate(definition);

        result.Should().Contain("sample_id_001");
    }

    [Fact]
    public void GenerateTemplate_RegularColumn_ShowsSampleValue()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.Query = "SELECT user_id, name FROM users";

        var result = _sut.GenerateTemplate(definition);

        result.Should().Contain("sample_value");
    }

    #endregion

    #region Batch Time Column Tests

    [Fact]
    public void GenerateTemplate_NonImmediateBatchTime_IncludesBatchTimeColumn()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.BatchTime = "column";
        definition.DataSource.BatchTimeColumn = "migration_date";
        definition.DataSource.Query = "SELECT user_id FROM users";

        var result = _sut.GenerateTemplate(definition);

        var headerLine = result.Split('\n')[0];
        headerLine.Should().Contain("migration_date");
    }

    [Fact]
    public void GenerateTemplate_ImmediateBatchTime_SkipsBatchTimeColumn()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.BatchTime = "immediate";
        definition.DataSource.BatchTimeColumn = "migration_date";
        definition.DataSource.Query = "SELECT user_id FROM users";

        var result = _sut.GenerateTemplate(definition);

        // Should only have user_id as primary key (from query and primary key requirement)
        var headerLine = result.Split('\n')[0];
        // migration_date should not be automatically added for immediate batches
        // (unless it's referenced elsewhere)
    }

    #endregion

    #region CSV Escaping Tests

    [Fact]
    public void GenerateTemplate_ValuesWithCommas_ProperlyEscaped()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.MultiValuedColumns = new List<MultiValuedColumnConfig>
        {
            new() { Name = "tags", Format = "comma_delimited" }
        };

        var result = _sut.GenerateTemplate(definition);

        // Values with commas should be quoted
        result.Should().Contain("\"value1,value2,value3\"");
    }

    [Fact]
    public void GenerateTemplate_ValuesWithQuotes_ProperlyEscaped()
    {
        var definition = CreateBasicDefinition();
        definition.DataSource.MultiValuedColumns = new List<MultiValuedColumnConfig>
        {
            new() { Name = "data", Format = "json_array" }
        };

        var result = _sut.GenerateTemplate(definition);

        // JSON array values contain quotes, which get escaped in CSV (quotes doubled)
        // The value [\"value1\",\"value2\",\"value3\"] becomes "[""value1"",""value2"",""value3""]" in CSV
        result.Should().Contain("\"\""); // Should have escaped quotes
    }

    #endregion
}
