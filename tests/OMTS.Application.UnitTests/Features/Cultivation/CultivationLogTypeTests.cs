using System.Text.Json;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The type of a cultivation log: the default, the refusal naming <c>LogType</c>, the list filter and the audit snapshot.</summary>
public class CultivationLogTypeTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly Today = new(2026, 11, 27);
    private static readonly DateOnly Visit = new(2026, 11, 20);

    private static (AppError? Error, CultivationLogValues Values) Read(string? logType, bool correction = false)
    {
        var error = CultivationLogValues.Read(
            Visit, logType, "Kiểm tra dây treo", [], otherFilePart: false, kept: 0, keysAreKnown: true, reason: correction ? "Sửa" : null, reasonRequired: correction, Start, Today, out var values);
        return (error, values);
    }

    [Theory]
    [InlineData("FEEDING")]
    [InlineData("CLEANING")]
    [InlineData("INSPECTION")]
    [InlineData("DISEASE")]
    [InlineData("REPAIR")]
    [InlineData("OTHER")]
    public void A_known_type_is_kept(string code)
    {
        var (error, values) = Read(code);

        Assert.Null(error);
        Assert.Equal(code, values.LogType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_type_becomes_other(string? logType)
    {
        var (error, values) = Read(logType);

        Assert.Null(error);
        Assert.Equal("OTHER", values.LogType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_correction_with_no_type_keeps_the_current_one(string? logType)
    {
        var (error, values) = Read(logType, correction: true);

        Assert.Null(error);
        Assert.Null(values.LogType);
    }

    [Fact]
    public void A_correction_with_a_type_replaces_it_and_an_unknown_one_is_refused()
    {
        var (error, values) = Read("repair", correction: true);
        Assert.Null(error);
        Assert.Equal("REPAIR", values.LogType);

        var (refused, _) = Read("SWIMMING", correction: true);
        Assert.Equal("LogType", refused!.Details!["field"]);
    }

    [Fact]
    public void A_type_in_another_letter_case_is_stored_as_its_code()
    {
        var (error, values) = Read(" cleaning ");

        Assert.Null(error);
        Assert.Equal("CLEANING", values.LogType);
    }

    [Fact]
    public void An_unknown_type_is_msg_gen_01_naming_log_type()
    {
        var (error, _) = Read("SWIMMING");

        Assert.NotNull(error);
        Assert.Equal(ErrorKind.Validation, error.Kind);
        Assert.Equal("LogType", error.Details!["field"]);
        Assert.Equal(["LogType"], (IReadOnlyList<string>)error.Details["fields"]!);
    }

    [Fact]
    public void An_unknown_type_is_listed_beside_the_other_invalid_fields()
    {
        var error = CultivationLogValues.Read(
            null, "SWIMMING", null, [], otherFilePart: false, kept: 0, keysAreKnown: true, reason: null, reasonRequired: false, Start, Today, out _);

        Assert.Equal(["Visit date", "LogType"], (IReadOnlyList<string>)error!.Details!["fields"]!);
    }

    [Fact]
    public void The_list_filter_is_absent_for_null_and_blank()
    {
        Assert.Null(CultivationLogValues.ReadTypeFilter(null, out var none));
        Assert.Null(none);
        Assert.Null(CultivationLogValues.ReadTypeFilter("  ", out var blank));
        Assert.Null(blank);
    }

    [Fact]
    public void The_list_filter_names_a_type_or_is_refused()
    {
        Assert.Null(CultivationLogValues.ReadTypeFilter("disease", out var wanted));
        Assert.Equal("DISEASE", wanted);

        var error = CultivationLogValues.ReadTypeFilter("SWIMMING", out var unknown);
        Assert.Null(unknown);
        Assert.Equal("LogType", error!.Details!["field"]);
    }

    [Fact]
    public void A_new_log_is_other_and_the_audit_snapshot_carries_the_type()
    {
        Assert.Equal("OTHER", new CultivationLog().LogType);

        var log = new CultivationLog { VisitDate = Visit, LogType = CultivationLogTypes.Feeding, Notes = "n" };
        var json = JsonSerializer.Serialize(CultivationLogValues.Snapshot(log));

        Assert.Contains("\"LogType\":\"FEEDING\"", json);
    }

    [Fact]
    public void A_change_of_type_alone_shows_in_the_audit_diff()
    {
        var log = new CultivationLog { VisitDate = Visit, LogType = CultivationLogTypes.Feeding };
        var before = CultivationLogValues.Snapshot(log);
        log.LogType = CultivationLogTypes.Repair;

        var (changedBefore, changedAfter) = OMTS.Application.Features.Audit.AuditDiff.Changed(before, CultivationLogValues.Snapshot(log));

        Assert.NotNull(changedBefore);
        Assert.Contains("FEEDING", JsonSerializer.Serialize(changedBefore));
        Assert.Contains("REPAIR", JsonSerializer.Serialize(changedAfter));
    }
}
