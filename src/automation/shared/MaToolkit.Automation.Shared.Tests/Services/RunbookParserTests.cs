using FluentAssertions;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MaToolkit.Automation.Shared.Tests.Services;

public class RunbookParserTests
{
    private readonly RunbookParser _sut;
    private readonly RunbookParser _sutWithPhaseEvaluator;
    private readonly Mock<ILogger<RunbookParser>> _loggerMock;
    private readonly Mock<IPhaseEvaluator> _phaseEvaluatorMock;

    public RunbookParserTests()
    {
        _loggerMock = new Mock<ILogger<RunbookParser>>();
        _phaseEvaluatorMock = new Mock<IPhaseEvaluator>();

        // Setup default successful parsing behavior
        _phaseEvaluatorMock.Setup(x => x.ParseOffsetMinutes(It.IsAny<string>())).Returns(0);
        _phaseEvaluatorMock.Setup(x => x.ParseDurationSeconds(It.IsAny<string>())).Returns(0);

        _sut = new RunbookParser(_loggerMock.Object);
        _sutWithPhaseEvaluator = new RunbookParser(_loggerMock.Object, _phaseEvaluatorMock.Object);
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

    #region Data Source Type Validation Tests

    [Fact]
    public void Validate_InvalidDataSourceType_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: mysql
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

        errors.Should().Contain(e => e.Contains("not supported") && e.Contains("mysql"));
    }

    [Fact]
    public void Validate_DataverseType_NoError()
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
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().NotContain(e => e.Contains("not supported"));
    }

    [Fact]
    public void Validate_DatabricksTypeWithWarehouse_NoError()
    {
        var yaml = @"
name: test
data_source:
  type: databricks
  connection: conn
  query: SELECT 1
  primary_key: id
  warehouse_id: abc123
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

        errors.Should().NotContain(e => e.Contains("not supported"));
        errors.Should().BeEmpty();
    }

    #endregion

    #region Multi-Valued Format Validation Tests

    [Fact]
    public void Validate_InvalidMultiValuedFormat_ReturnsError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
  multi_valued_columns:
    - name: groups
      format: pipe_delimited
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

        errors.Should().Contain(e => e.Contains("invalid format") && e.Contains("pipe_delimited"));
    }

    [Fact]
    public void Validate_ValidMultiValuedFormats_NoError()
    {
        var yaml = @"
name: test
data_source:
  type: dataverse
  connection: conn
  query: SELECT 1
  primary_key: id
  batch_time: immediate
  multi_valued_columns:
    - name: groups
      format: semicolon_delimited
    - name: aliases
      format: comma_delimited
    - name: tags
      format: json_array
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

        errors.Should().NotContain(e => e.Contains("invalid format"));
    }

    #endregion

    #region Duplicate Name Validation Tests

    [Fact]
    public void Validate_DuplicatePhaseNames_ReturnsError()
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
  - name: migrate
    offset: T-1d
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
  - name: migrate
    offset: T-0
    steps:
      - name: step2
        worker_id: worker1
        function: DoWork2
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("Duplicate phase name") && e.Contains("migrate"));
    }

    [Fact]
    public void Validate_DuplicateStepNamesInPhase_ReturnsError()
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
      - name: do-work
        worker_id: worker1
        function: DoWork
      - name: do-work
        worker_id: worker2
        function: DoWorkAgain
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("duplicate step name") && e.Contains("do-work"));
    }

    [Fact]
    public void Validate_SameStepNameDifferentPhases_NoError()
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
    offset: T-1d
    steps:
      - name: notify
        worker_id: worker1
        function: SendNotification
  - name: phase2
    offset: T-0
    steps:
      - name: notify
        worker_id: worker1
        function: SendNotification
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().NotContain(e => e.Contains("duplicate step name"));
    }

    #endregion

    #region Offset Parsing Validation Tests

    [Fact]
    public void Validate_UnparseableOffset_ReturnsError()
    {
        _phaseEvaluatorMock.Setup(x => x.ParseOffsetMinutes("T-abc"))
            .Throws(new ArgumentException("Invalid offset format: T-abc"));

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
    offset: T-abc
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
";
        var definition = _sutWithPhaseEvaluator.Parse(yaml);

        var errors = _sutWithPhaseEvaluator.Validate(definition);

        // Should get both regex validation error and parsing error
        errors.Should().Contain(e => e.Contains("invalid offset format") || e.Contains("cannot be parsed"));
    }

    [Fact]
    public void Validate_ValidOffsets_NoError()
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
    offset: T-1d
    steps:
      - name: step1
        worker_id: worker1
        function: DoWork
  - name: phase2
    offset: T-24h
    steps:
      - name: step2
        worker_id: worker1
        function: DoWork
  - name: phase3
    offset: T-0
    steps:
      - name: step3
        worker_id: worker1
        function: DoWork
";
        var definition = _sutWithPhaseEvaluator.Parse(yaml);

        var errors = _sutWithPhaseEvaluator.Validate(definition);

        errors.Should().NotContain(e => e.Contains("offset"));
    }

    #endregion

    #region Duration Parsing Validation Tests

    [Fact]
    public void Validate_UnparseablePollInterval_ReturnsError()
    {
        _phaseEvaluatorMock.Setup(x => x.ParseDurationSeconds("abc"))
            .Throws(new ArgumentException("Invalid duration format: abc"));

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
          interval: abc
          timeout: 1h
";
        var definition = _sutWithPhaseEvaluator.Parse(yaml);

        var errors = _sutWithPhaseEvaluator.Validate(definition);

        errors.Should().Contain(e => e.Contains("poll.interval") && e.Contains("cannot be parsed"));
    }

    [Fact]
    public void Validate_UnparseablePollTimeout_ReturnsError()
    {
        _phaseEvaluatorMock.Setup(x => x.ParseDurationSeconds("xyz"))
            .Throws(new ArgumentException("Invalid duration format: xyz"));

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
          interval: 5m
          timeout: xyz
";
        var definition = _sutWithPhaseEvaluator.Parse(yaml);

        var errors = _sutWithPhaseEvaluator.Validate(definition);

        errors.Should().Contain(e => e.Contains("poll.timeout") && e.Contains("cannot be parsed"));
    }

    [Fact]
    public void Validate_ValidPollConfig_NoError()
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
          interval: 5m
          timeout: 1h
";
        var definition = _sutWithPhaseEvaluator.Parse(yaml);

        var errors = _sutWithPhaseEvaluator.Validate(definition);

        errors.Should().NotContain(e => e.Contains("poll.interval"));
        errors.Should().NotContain(e => e.Contains("poll.timeout"));
    }

    #endregion

    #region Template Syntax Validation Tests

    [Fact]
    public void Validate_UnclosedTemplateBraces_ReturnsError()
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
        params:
          user_id: '{{user_id'
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("unclosed template braces"));
    }

    [Fact]
    public void Validate_ValidTemplates_NoError()
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
        params:
          user_id: '{{user_id}}'
          batch_id: '{{_batch_id}}'
          combined: 'User {{name}} ({{email}})'
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().NotContain(e => e.Contains("template"));
    }

    [Fact]
    public void Validate_UnmatchedClosingBraces_ReturnsError()
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
        params:
          user_id: 'value}}'
";
        var definition = _sut.Parse(yaml);

        var errors = _sut.Validate(definition);

        errors.Should().Contain(e => e.Contains("unmatched closing braces"));
    }

    #endregion
}
