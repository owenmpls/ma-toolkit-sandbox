using FluentAssertions;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MaToolkit.Automation.Shared.Tests.Services;

public class RunbookParserTests
{
    private readonly RunbookParser _sut;
    private readonly Mock<ILogger<RunbookParser>> _loggerMock;

    public RunbookParserTests()
    {
        _loggerMock = new Mock<ILogger<RunbookParser>>();
        _sut = new RunbookParser(_loggerMock.Object);
    }

    #region Parse Tests

    [Fact]
    public void Parse_ValidYaml_ReturnsRunbookDefinition()
    {
        var yaml = @"
name: test-runbook
description: A test runbook
data_source:
  type: dataverse
  connection: DATAVERSE_CONNECTION_STRING
  query: SELECT * FROM users
  primary_key: user_id
  batch_time_column: migration_date
phases:
  - name: pre-migration
    offset: T-1d
    steps:
      - name: notify-user
        worker_id: email-worker
        function: SendEmail
        params:
          template: pre-migration-notice
";

        var result = _sut.Parse(yaml);

        result.Name.Should().Be("test-runbook");
        result.Description.Should().Be("A test runbook");
        result.DataSource.Type.Should().Be("dataverse");
        result.DataSource.PrimaryKey.Should().Be("user_id");
        result.Phases.Should().HaveCount(1);
        result.Phases[0].Name.Should().Be("pre-migration");
        result.Phases[0].Offset.Should().Be("T-1d");
        result.Phases[0].Steps.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_WithInitSteps_ParsesInitSection()
    {
        var yaml = @"
name: test-runbook
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
init:
  - name: setup-batch
    worker_id: admin-worker
    function: SetupBatch
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
";

        var result = _sut.Parse(yaml);

        result.Init.Should().HaveCount(1);
        result.Init[0].Name.Should().Be("setup-batch");
        result.Init[0].Function.Should().Be("SetupBatch");
    }

    [Fact]
    public void Parse_WithRollbacks_ParsesRollbackSection()
    {
        var yaml = @"
name: test-runbook
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
        on_failure: rollback1
rollbacks:
  rollback1:
    - name: undo-step1
      worker_id: worker1
      function: UndoWork
";

        var result = _sut.Parse(yaml);

        result.Rollbacks.Should().ContainKey("rollback1");
        result.Rollbacks["rollback1"].Should().HaveCount(1);
        result.Rollbacks["rollback1"][0].Function.Should().Be("UndoWork");
    }

    [Fact]
    public void Parse_WithPollConfig_ParsesPollSection()
    {
        var yaml = @"
name: test-runbook
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: long-running-step
        worker_id: worker1
        function: LongRunningTask
        poll:
          interval: 5m
          timeout: 1h
";

        var result = _sut.Parse(yaml);

        result.Phases[0].Steps[0].Poll.Should().NotBeNull();
        result.Phases[0].Steps[0].Poll!.Interval.Should().Be("5m");
        result.Phases[0].Steps[0].Poll!.Timeout.Should().Be("1h");
    }

    [Fact]
    public void Parse_WithMultiValuedColumns_ParsesCorrectly()
    {
        var yaml = @"
name: test-runbook
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
  multi_valued_columns:
    - name: group_memberships
      format: semicolon_delimited
    - name: aliases
      format: json_array
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
";

        var result = _sut.Parse(yaml);

        result.DataSource.MultiValuedColumns.Should().HaveCount(2);
        result.DataSource.MultiValuedColumns[0].Name.Should().Be("group_memberships");
        result.DataSource.MultiValuedColumns[0].Format.Should().Be("semicolon_delimited");
    }

    #endregion

    #region Validate Tests

    [Fact]
    public void Validate_MissingName_ReturnsError()
    {
        var yaml = @"
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("name is required"));
    }

    [Fact]
    public void Validate_MissingDataSource_ReturnsErrors()
    {
        var yaml = @"
name: test
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        // When data_source is empty/default, validation returns errors for required fields
        errors.Should().Contain(e => e.Contains("data_source.type is required"));
        errors.Should().Contain(e => e.Contains("data_source.primary_key is required"));
    }

    [Fact]
    public void Validate_MissingPhases_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain("At least one phase is required");
    }

    [Fact]
    public void Validate_InvalidPhaseOffset_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: invalid-offset
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("invalid offset format"));
    }

    [Fact]
    public void Validate_MissingStepWorkerId_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        function: DoWork
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("worker_id is required"));
    }

    [Fact]
    public void Validate_MissingStepFunction_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("function is required"));
    }

    [Fact]
    public void Validate_InvalidRollbackReference_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
        on_failure: nonexistent-rollback
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("unknown rollback"));
    }

    [Fact]
    public void Validate_DatabricksWithoutWarehouseId_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: databricks
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("warehouse_id is required for databricks"));
    }

    [Fact]
    public void Validate_ValidRunbook_ReturnsNoErrors()
    {
        var yaml = @"
name: valid-runbook
data_source:
  type: dataverse
  connection: DATAVERSE_CONNECTION
  query: SELECT user_id, email FROM users
  primary_key: user_id
  batch_time: immediate
phases:
  - name: migrate
    offset: T-0
    steps:
      - name: migrate-mailbox
        worker_id: exchange-worker
        function: MigrateMailbox
        params:
          user_id: '{{user_id}}'
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_PollStepWithoutInterval_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
phases:
  - name: phase1
    offset: T-0
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
        poll:
          timeout: 1h
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("poll.interval is required"));
    }

    #endregion
}
