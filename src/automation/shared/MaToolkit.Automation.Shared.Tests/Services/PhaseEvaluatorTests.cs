using FluentAssertions;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MaToolkit.Automation.Shared.Tests.Services;

public class PhaseEvaluatorTests
{
    private readonly PhaseEvaluator _sut;
    private readonly Mock<ILogger<PhaseEvaluator>> _loggerMock;

    public PhaseEvaluatorTests()
    {
        _loggerMock = new Mock<ILogger<PhaseEvaluator>>();
        _sut = new PhaseEvaluator(_loggerMock.Object);
    }

    #region ParseOffsetMinutes Tests

    [Fact]
    public void ParseOffsetMinutes_T0_ReturnsZero()
    {
        var result = _sut.ParseOffsetMinutes("T-0");
        result.Should().Be(0);
    }

    [Theory]
    [InlineData("T-1d", 1440)]    // 1 day = 1440 minutes
    [InlineData("T-7d", 10080)]   // 7 days
    [InlineData("T-30d", 43200)]  // 30 days
    public void ParseOffsetMinutes_Days_ConvertsCorrectly(string offset, int expectedMinutes)
    {
        var result = _sut.ParseOffsetMinutes(offset);
        result.Should().Be(expectedMinutes);
    }

    [Theory]
    [InlineData("T-1h", 60)]
    [InlineData("T-24h", 1440)]
    [InlineData("T-48h", 2880)]
    public void ParseOffsetMinutes_Hours_ConvertsCorrectly(string offset, int expectedMinutes)
    {
        var result = _sut.ParseOffsetMinutes(offset);
        result.Should().Be(expectedMinutes);
    }

    [Theory]
    [InlineData("T-1m", 1)]
    [InlineData("T-60m", 60)]
    [InlineData("T-120m", 120)]
    public void ParseOffsetMinutes_Minutes_ConvertsCorrectly(string offset, int expectedMinutes)
    {
        var result = _sut.ParseOffsetMinutes(offset);
        result.Should().Be(expectedMinutes);
    }

    [Theory]
    [InlineData("T-30s", 1)]   // 30 seconds rounds up to 1 minute
    [InlineData("T-60s", 1)]   // 60 seconds = 1 minute
    [InlineData("T-90s", 2)]   // 90 seconds rounds up to 2 minutes
    [InlineData("T-120s", 2)]  // 120 seconds = 2 minutes
    public void ParseOffsetMinutes_Seconds_RoundsUpToMinutes(string offset, int expectedMinutes)
    {
        var result = _sut.ParseOffsetMinutes(offset);
        result.Should().Be(expectedMinutes);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("5d")]
    [InlineData("T5d")]
    [InlineData("T+5d")]
    public void ParseOffsetMinutes_InvalidFormat_ThrowsArgumentException(string offset)
    {
        var act = () => _sut.ParseOffsetMinutes(offset);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("T-5x")]
    [InlineData("T-5w")]
    public void ParseOffsetMinutes_InvalidSuffix_ThrowsArgumentException(string offset)
    {
        var act = () => _sut.ParseOffsetMinutes(offset);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("T-abcd")]
    [InlineData("T-1.5d")]
    public void ParseOffsetMinutes_InvalidNumber_ThrowsFormatException(string offset)
    {
        var act = () => _sut.ParseOffsetMinutes(offset);
        act.Should().Throw<FormatException>();
    }

    #endregion

    #region ParseDurationSeconds Tests

    [Theory]
    [InlineData("1d", 86400)]    // 1 day = 86400 seconds
    [InlineData("7d", 604800)]   // 7 days
    public void ParseDurationSeconds_Days_ConvertsCorrectly(string duration, int expectedSeconds)
    {
        var result = _sut.ParseDurationSeconds(duration);
        result.Should().Be(expectedSeconds);
    }

    [Theory]
    [InlineData("1h", 3600)]
    [InlineData("24h", 86400)]
    public void ParseDurationSeconds_Hours_ConvertsCorrectly(string duration, int expectedSeconds)
    {
        var result = _sut.ParseDurationSeconds(duration);
        result.Should().Be(expectedSeconds);
    }

    [Theory]
    [InlineData("1m", 60)]
    [InlineData("5m", 300)]
    public void ParseDurationSeconds_Minutes_ConvertsCorrectly(string duration, int expectedSeconds)
    {
        var result = _sut.ParseDurationSeconds(duration);
        result.Should().Be(expectedSeconds);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("60s", 60)]
    public void ParseDurationSeconds_Seconds_ConvertsCorrectly(string duration, int expectedSeconds)
    {
        var result = _sut.ParseDurationSeconds(duration);
        result.Should().Be(expectedSeconds);
    }

    [Fact]
    public void ParseDurationSeconds_Empty_ReturnsZero()
    {
        var result = _sut.ParseDurationSeconds("");
        result.Should().Be(0);
    }

    [Fact]
    public void ParseDurationSeconds_Null_ReturnsZero()
    {
        var result = _sut.ParseDurationSeconds(null!);
        result.Should().Be(0);
    }

    [Theory]
    [InlineData("5x")]
    [InlineData("5w")]
    public void ParseDurationSeconds_InvalidSuffix_ThrowsArgumentException(string duration)
    {
        var act = () => _sut.ParseDurationSeconds(duration);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseDurationSeconds_TooShort_ThrowsArgumentException()
    {
        var act = () => _sut.ParseDurationSeconds("5");
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region CalculateDueAt Tests

    [Fact]
    public void CalculateDueAt_ZeroOffset_ReturnsSameTime()
    {
        var batchStartTime = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = _sut.CalculateDueAt(batchStartTime, 0);
        result.Should().Be(batchStartTime);
    }

    [Fact]
    public void CalculateDueAt_PositiveOffset_SubtractsMinutes()
    {
        var batchStartTime = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = _sut.CalculateDueAt(batchStartTime, 60);  // T-1h
        result.Should().Be(new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void CalculateDueAt_DaysOffset_SubtractsDays()
    {
        var batchStartTime = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = _sut.CalculateDueAt(batchStartTime, 1440);  // T-1d
        result.Should().Be(new DateTime(2024, 6, 14, 10, 0, 0, DateTimeKind.Utc));
    }

    #endregion

    #region CreatePhaseExecutions Tests

    [Fact]
    public void CreatePhaseExecutions_CreatesRecordForEachPhase()
    {
        var definition = new RunbookDefinition
        {
            Phases = new List<PhaseDefinition>
            {
                new() { Name = "phase1", Offset = "T-1d" },
                new() { Name = "phase2", Offset = "T-1h" }
            }
        };
        var batchStartTime = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var result = _sut.CreatePhaseExecutions(1, batchStartTime, definition, 1);

        result.Should().HaveCount(2);
        result[0].PhaseName.Should().Be("phase1");
        result[0].OffsetMinutes.Should().Be(1440);
        result[0].Status.Should().Be(PhaseStatus.Pending);
        result[1].PhaseName.Should().Be("phase2");
        result[1].OffsetMinutes.Should().Be(60);
    }

    [Fact]
    public void CreatePhaseExecutions_EmptyPhases_ReturnsEmptyList()
    {
        var definition = new RunbookDefinition { Phases = new List<PhaseDefinition>() };
        var batchStartTime = DateTime.UtcNow;

        var result = _sut.CreatePhaseExecutions(1, batchStartTime, definition, 1);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CreatePhaseExecutions_SetsCorrectBatchIdAndVersion()
    {
        var definition = new RunbookDefinition
        {
            Phases = new List<PhaseDefinition>
            {
                new() { Name = "phase1", Offset = "T-0" }
            }
        };

        var result = _sut.CreatePhaseExecutions(42, DateTime.UtcNow, definition, 5);

        result[0].BatchId.Should().Be(42);
        result[0].RunbookVersion.Should().Be(5);
    }

    #endregion

    #region HandleVersionTransition Tests

    [Fact]
    public void HandleVersionTransition_NoOldVersionPhases_ReturnsEmpty()
    {
        var existingPhases = new List<PhaseExecutionRecord>
        {
            new() { PhaseName = "phase1", RunbookVersion = 2 }
        };
        var definition = new RunbookDefinition
        {
            Phases = new List<PhaseDefinition>
            {
                new() { Name = "phase1", Offset = "T-0" }
            }
        };

        var result = _sut.HandleVersionTransition(
            existingPhases, 1, DateTime.UtcNow, definition, 2, "rerun", false);

        result.Should().BeEmpty();
    }

    [Fact]
    public void HandleVersionTransition_WithOldVersionPhases_CreatesNewPhases()
    {
        // Note: The caller (SchedulerTimerFunction) checks for hasCurrentVersionPhases
        // before calling HandleVersionTransition. This method only checks for old phases.
        var existingPhases = new List<PhaseExecutionRecord>
        {
            new() { PhaseName = "phase1", RunbookVersion = 1 },
            new() { PhaseName = "phase1", RunbookVersion = 2 }
        };
        var definition = new RunbookDefinition
        {
            Phases = new List<PhaseDefinition>
            {
                new() { Name = "phase1", Offset = "T-0" }
            }
        };

        var result = _sut.HandleVersionTransition(
            existingPhases, 1, DateTime.UtcNow, definition, 2, "rerun", false);

        // Returns new phases because existingSet has old version phases
        result.Should().HaveCount(1);
        result[0].RunbookVersion.Should().Be(2);
    }

    [Fact]
    public void HandleVersionTransition_OldVersionOnly_CreatesNewPhases()
    {
        var existingPhases = new List<PhaseExecutionRecord>
        {
            new() { PhaseName = "phase1", RunbookVersion = 1 }
        };
        var batchStartTime = DateTime.UtcNow.AddDays(7);  // Future batch
        var definition = new RunbookDefinition
        {
            Phases = new List<PhaseDefinition>
            {
                new() { Name = "phase1", Offset = "T-1d" },
                new() { Name = "phase2", Offset = "T-1h" }
            }
        };

        var result = _sut.HandleVersionTransition(
            existingPhases, 1, batchStartTime, definition, 2, "rerun", false);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(p => p.RunbookVersion == 2);
        result.Should().OnlyContain(p => p.Status == PhaseStatus.Pending);
    }

    [Fact]
    public void HandleVersionTransition_OverdueWithIgnoreMode_SkipsOverduePhases()
    {
        var existingPhases = new List<PhaseExecutionRecord>
        {
            new() { PhaseName = "phase1", RunbookVersion = 1 }
        };
        var batchStartTime = DateTime.UtcNow.AddHours(-2);  // Past batch
        var definition = new RunbookDefinition
        {
            Phases = new List<PhaseDefinition>
            {
                new() { Name = "phase1", Offset = "T-1h" }  // Due 1 hour before, so overdue
            }
        };

        var result = _sut.HandleVersionTransition(
            existingPhases, 1, batchStartTime, definition, 2, "ignore", false);

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(PhaseStatus.Skipped);
    }

    [Fact]
    public void HandleVersionTransition_OverdueWithRerunMode_KeepsPending()
    {
        var existingPhases = new List<PhaseExecutionRecord>
        {
            new() { PhaseName = "phase1", RunbookVersion = 1 }
        };
        var batchStartTime = DateTime.UtcNow.AddHours(-2);
        var definition = new RunbookDefinition
        {
            Phases = new List<PhaseDefinition>
            {
                new() { Name = "phase1", Offset = "T-1h" }
            }
        };

        var result = _sut.HandleVersionTransition(
            existingPhases, 1, batchStartTime, definition, 2, "rerun", false);

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(PhaseStatus.Pending);
    }

    #endregion
}
