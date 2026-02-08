using System.Text;
using FluentAssertions;
using AdminApi.Functions.Services;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AdminApi.Functions.Tests.Services;

public class CsvUploadServiceTests
{
    private readonly CsvUploadService _sut;
    private readonly Mock<ILogger<CsvUploadService>> _loggerMock;

    public CsvUploadServiceTests()
    {
        _loggerMock = new Mock<ILogger<CsvUploadService>>();
        _sut = new CsvUploadService(_loggerMock.Object);
    }

    private static RunbookDefinition CreateDefinition(string primaryKey = "user_id")
    {
        return new RunbookDefinition
        {
            Name = "test-runbook",
            DataSource = new DataSourceConfig
            {
                Type = "dataverse",
                PrimaryKey = primaryKey,
                Query = "SELECT user_id, email FROM users"
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
                            Function = "DoWork",
                            Params = new Dictionary<string, string>
                            {
                                ["user_id"] = "{{user_id}}",
                                ["email"] = "{{email}}"
                            }
                        }
                    }
                }
            }
        };
    }

    private static Stream CreateCsvStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    #region Basic Parsing Tests

    [Fact]
    public async Task ParseCsvAsync_ValidCsv_ReturnsSuccess()
    {
        var csv = "user_id,email\nuser1,user1@example.com\nuser2,user2@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.RowCount.Should().Be(2);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseCsvAsync_EmptyCsv_ReturnsError()
    {
        var csv = "";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain("CSV file is empty");
    }

    [Fact]
    public async Task ParseCsvAsync_HeaderOnly_ReturnsZeroRows()
    {
        var csv = "user_id,email";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.RowCount.Should().Be(0);
    }

    [Fact]
    public async Task ParseCsvAsync_CorrectColumnValues_ParsedInDataTable()
    {
        var csv = "user_id,email,name\nuser1,user1@example.com,John Doe";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Columns.Count.Should().Be(3);
        result.Data.Rows[0]["user_id"].Should().Be("user1");
        result.Data.Rows[0]["email"].Should().Be("user1@example.com");
        result.Data.Rows[0]["name"].Should().Be("John Doe");
    }

    #endregion

    #region Primary Key Validation Tests

    [Fact]
    public async Task ParseCsvAsync_MissingPrimaryKeyColumn_ReturnsError()
    {
        var csv = "email,name\nuser1@example.com,John";
        var definition = CreateDefinition(primaryKey: "user_id");

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Primary key column") && e.Contains("user_id"));
    }

    [Fact]
    public async Task ParseCsvAsync_EmptyPrimaryKeyValue_ReturnsError()
    {
        var csv = "user_id,email\n,user1@example.com\nuser2,user2@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Primary key cannot be empty"));
    }

    [Fact]
    public async Task ParseCsvAsync_DuplicatePrimaryKeys_ReturnsError()
    {
        var csv = "user_id,email\nuser1,user1@example.com\nuser1,duplicate@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Duplicate primary key") && e.Contains("user1"));
    }

    [Fact]
    public async Task ParseCsvAsync_CaseInsensitivePrimaryKeyColumn_Works()
    {
        var csv = "USER_ID,email\nuser1,user1@example.com";
        var definition = CreateDefinition(primaryKey: "user_id");

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.RowCount.Should().Be(1);
    }

    #endregion

    #region Required Column Validation Tests

    [Fact]
    public async Task ParseCsvAsync_MissingRequiredColumn_ReturnsError()
    {
        // CSV has user_id but missing email (referenced in step template {{email}})
        var csv = "user_id\nuser1";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Required column") && e.Contains("email"));
    }

    [Fact]
    public async Task ParseCsvAsync_MissingMultipleRequiredColumns_ReportsAll()
    {
        // CSV has only a non-PK column; both user_id-template and email columns are "required"
        // but user_id is the PK so it passes PK check — we need a CSV with PK present but other columns missing
        var definition = new RunbookDefinition
        {
            Name = "test-runbook",
            DataSource = new DataSourceConfig
            {
                Type = "dataverse",
                PrimaryKey = "user_id",
                Query = "SELECT user_id, email, display_name FROM users"
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
                            Function = "DoWork",
                            Params = new Dictionary<string, string>
                            {
                                ["email"] = "{{email}}",
                                ["name"] = "{{display_name}}"
                            }
                        }
                    }
                }
            }
        };
        var csv = "user_id\nuser1";

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterOrEqualTo(2);
        result.Errors.Should().Contain(e => e.Contains("email"));
        result.Errors.Should().Contain(e => e.Contains("display_name"));
    }

    [Fact]
    public async Task ParseCsvAsync_CaseInsensitiveColumnMatch_Succeeds()
    {
        // CSV has EMAIL (uppercase), definition expects email (lowercase via template)
        var csv = "USER_ID,EMAIL\nuser1,user1@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.RowCount.Should().Be(1);
    }

    [Fact]
    public async Task ParseCsvAsync_MissingBatchTimeColumn_ReturnsError()
    {
        var definition = new RunbookDefinition
        {
            Name = "test-runbook",
            DataSource = new DataSourceConfig
            {
                Type = "dataverse",
                PrimaryKey = "user_id",
                BatchTimeColumn = "migration_date",
                Query = "SELECT user_id, email, migration_date FROM users"
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
                            Function = "DoWork",
                            Params = new Dictionary<string, string>
                            {
                                ["email"] = "{{email}}"
                            }
                        }
                    }
                }
            }
        };
        var csv = "user_id,email\nuser1,user1@example.com";

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Required column") && e.Contains("migration_date"));
    }

    #endregion

    #region CSV Parsing Edge Cases

    [Fact]
    public async Task ParseCsvAsync_QuotedValues_ParsedCorrectly()
    {
        var csv = "user_id,email,description\nuser1,user1@example.com,\"Hello, World\"";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.Data!.Rows[0]["description"].Should().Be("Hello, World");
    }

    [Fact]
    public async Task ParseCsvAsync_EscapedQuotes_ParsedCorrectly()
    {
        var csv = "user_id,email,description\nuser1,user1@example.com,\"He said \"\"Hello\"\"\"";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.Data!.Rows[0]["description"].Should().Be("He said \"Hello\"");
    }

    [Fact]
    public async Task ParseCsvAsync_FewerColumnsThanHeader_PadsWithEmpty()
    {
        var csv = "user_id,email,name\nuser1,user1@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.Data!.Rows[0]["name"].Should().Be("");
    }

    [Fact]
    public async Task ParseCsvAsync_WindowsLineEndings_ParsedCorrectly()
    {
        var csv = "user_id,email\r\nuser1,user1@example.com\r\nuser2,user2@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.RowCount.Should().Be(2);
    }

    [Fact]
    public async Task ParseCsvAsync_WhitespaceInValues_Trimmed()
    {
        var csv = "user_id,email\n  user1  ,  user1@example.com  ";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.Data!.Rows[0]["user_id"].Should().Be("user1");
        result.Data.Rows[0]["email"].Should().Be("user1@example.com");
    }

    #endregion

    #region Warning Tests

    [Fact]
    public async Task ParseCsvAsync_UnexpectedColumn_AddsWarning()
    {
        var csv = "user_id,email,unexpected_column\nuser1,user1@example.com,value";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Contains("Unexpected column") && w.Contains("unexpected_column"));
    }

    [Fact]
    public async Task ParseCsvAsync_ExpectedColumnFromTemplate_NoWarning()
    {
        var csv = "user_id,email\nuser1,user1@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeTrue();
        result.Warnings.Should().NotContain(w => w.Contains("user_id"));
        result.Warnings.Should().NotContain(w => w.Contains("email"));
    }

    #endregion

    #region Multiple Row Tests

    [Fact]
    public async Task ParseCsvAsync_ManyRows_AllParsed()
    {
        var sb = new StringBuilder("user_id,email\n");
        for (int i = 1; i <= 100; i++)
        {
            sb.AppendLine($"user{i},user{i}@example.com");
        }
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(sb.ToString()), definition);

        result.Success.Should().BeTrue();
        result.RowCount.Should().Be(100);
    }

    [Fact]
    public async Task ParseCsvAsync_MultipleErrors_AllReported()
    {
        var csv = "user_id,email\n,empty@example.com\nuser1,user1@example.com\n,another_empty@example.com";
        var definition = CreateDefinition();

        var result = await _sut.ParseCsvAsync(CreateCsvStream(csv), definition);

        result.Success.Should().BeFalse();
        result.Errors.Count.Should().Be(2);
    }

    #endregion
}
