using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Application.Features.WorkTracking;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.LaborCost;

/// <summary>Field rules of seasonal workers (LAB-02) and work assignments (LAB-03).</summary>
public class WorkforceInputTests
{
    [Fact]
    public void Worker_needs_a_phone_or_a_citizen_id_and_the_phone_is_normalised()
    {
        var (none, noneInvalid) = WorkerInput.From(new SaveWorkerRequest("Trần Văn Bé", null, null, null, null));
        var (phone, _) = WorkerInput.From(new SaveWorkerRequest("  Trần Văn Bé ", "+84 912 345 678", null, null, null));
        var (_, badCitizen) = WorkerInput.From(new SaveWorkerRequest("Trần Văn Bé", null, "12345", null, null));
        var (citizen, _) = WorkerInput.From(new SaveWorkerRequest("Trần Văn Bé", null, "079123456789", null, null));

        Assert.Null(none);
        Assert.Equal([WorkerInput.ContactField], noneInvalid);
        Assert.Equal(("Trần Văn Bé", "0912345678"), (phone!.FullName, phone.PhoneNumber));
        Assert.Equal([WorkerInput.CitizenIdField], badCitizen);
        Assert.Equal("079123456789", citizen!.CitizenId);
    }

    [Fact]
    public void Piecework_needs_unit_plan_and_whole_rate()
    {
        var (_, invalid) = WorkAssignmentInput.From(new SaveWorkAssignmentRequest(
            new DateOnly(2026, 10, 24), null, Guid.NewGuid(), null, "Clean cages", null, WorkPaymentType.Piecework,
            PieceUnit: null, PlannedQuantity: 0, PieceRate: 2_500.5m, ExpectedCompletionDate: new DateOnly(2026, 10, 20)));

        Assert.Equal(["Piece unit", "Planned quantity", "Piece rate", "Expected completion date"], invalid);
    }

    [Fact]
    public void Daily_wage_refuses_piece_fields_and_needs_exactly_one_assignee()
    {
        var (_, invalid) = WorkAssignmentInput.From(new SaveWorkAssignmentRequest(
            new DateOnly(2026, 10, 24), Guid.NewGuid(), Guid.NewGuid(), null, "Clean cages", null, WorkPaymentType.DailyWage, PieceRate: 500));

        Assert.Equal([WorkAssignmentInput.AssigneeField, "Piece rate"], invalid);
    }

    [Fact]
    public void Piece_rate_may_be_any_positive_whole_number()
    {
        var (input, invalid) = WorkAssignmentInput.From(new SaveWorkAssignmentRequest(
            new DateOnly(2026, 10, 24), null, null, Guid.NewGuid(), "Tie ropes", " Notes ", WorkPaymentType.Piecework,
            PieceUnit: "rope", PlannedQuantity: 40.25m, PieceRate: 500));

        Assert.Empty(invalid);
        Assert.Equal((500m, 40.25m, "Notes"), (input!.PieceRate!.Value, input.PlannedQuantity!.Value, input.Notes));
    }
}
