using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-05 piecework completion: amount rounding, refused assignments and values, re-confirmation, lock and evidence.</summary>
public class PieceworkCompletionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    [Fact]
    public async Task Amount_is_quantity_times_rate_rounded_up_to_a_thousand()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(10);
        var piece = await w.AddPieceworkAsync(date, null, worker.Id, null, rate: 2_500);

        var confirmed = await ExpectAsync(await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion",
            new { completedQuantity = 37, completionDate = date.AddDays(1), completionNote = "Đạt" }), HttpStatusCode.OK);

        var completion = confirmed.GetProperty("completion");
        Assert.Equal(("Completed", 93_000m), (confirmed.GetProperty("state").GetString(), completion.GetProperty("pieceworkAmount").GetDecimal()));
        Assert.Contains(await factory.AuditOfAsync(piece.Id), e => e.Action == "WorkAssignment.CompletionConfirmed");
    }

    [Fact]
    public async Task Daily_wage_or_cancelled_assignments_cannot_be_confirmed_MSG61()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(6);
        var daily = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments,
            new { workDate = date, workerId = worker.Id, task = "Trực bè", paymentType = "DailyWage" }), HttpStatusCode.Created);
        var cancelled = await w.AddPieceworkAsync(date, null, worker.Id, null);
        await factory.UpdateAsync<WorkAssignment>(cancelled.Id, a => (a.CancelledAt, a.CancellationReason) = (DateTimeOffset.UtcNow, "x"));
        var body = new { completedQuantity = 5, completionDate = date };

        var dailyProblem = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{daily.GetProperty("id").GetGuid()}/completion", body))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG61");
        var cancelledProblem = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{cancelled.Id}/completion", body))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG61");

        Assert.Equal("Cannot change Work assignment from “Active” to “Completed”: only active piecework can be confirmed.",
            dailyProblem.GetProperty("detail").GetString());
        Assert.Equal("Cannot change Work assignment from “Cancelled” to “Completed”: only active piecework can be confirmed.",
            cancelledProblem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Negative_quantity_and_completion_before_the_work_date_are_MSG01()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(15);
        var piece = await w.AddPieceworkAsync(date, null, worker.Id, null);

        var negative = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion", new { completedQuantity = -1, completionDate = date }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var early = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion", new { completedQuantity = 3, completionDate = date.AddDays(-1) }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var zero = await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion", new { completedQuantity = 0, completionDate = date });

        Assert.Equal("Completed quantity", negative.Field());
        Assert.Equal("Completion date", early.Field());
        Assert.Equal(0m, (await ExpectAsync(zero, HttpStatusCode.OK)).GetProperty("completion").GetProperty("pieceworkAmount").GetDecimal());
    }

    [Fact]
    public async Task Completion_date_in_a_finalized_month_is_locked()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var month = NewMonth();
        var piece = await w.AddPieceworkAsync(month.Day(25), null, worker.Id, null);
        await w.FinalizeMonthAsync(month);

        await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion", new { completedQuantity = 3, completionDate = month.Day(26) }))
            .ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
    }

    [Fact]
    public async Task Re_confirming_needs_a_reason_kept_in_the_audit()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(11);
        var piece = await w.AddPieceworkAsync(date, null, worker.Id, null, completed: 10);

        var noReason = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion", new { completedQuantity = 12, completionDate = date }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var again = await ExpectAsync(await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion",
            new { completedQuantity = 12, completionDate = date, reason = "Đếm lại" }), HttpStatusCode.OK);

        Assert.Equal("Reason", noReason.Field());
        Assert.Equal(30_000m, again.GetProperty("completion").GetProperty("pieceworkAmount").GetDecimal());
        Assert.Contains(await factory.AuditOfAsync(piece.Id), e => e.Action == "WorkAssignment.CompletionConfirmed" && e.Reason == "Đếm lại");
    }

    [Fact]
    public async Task Evidence_photos_are_stored_up_to_five()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var piece = await w.AddPieceworkAsync(NewMonth().Day(12), null, worker.Id, null, completed: 4);

        var saved = await ExpectAsync(await w.Owner.PostAsync($"{w.Assignments}/{piece.Id}/completion/evidence", Photos(2)), HttpStatusCode.OK);
        var tooMany = await (await w.Owner.PostAsync($"{w.Assignments}/{piece.Id}/completion/evidence", Photos(4)))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal(2, saved.GetProperty("completion").GetProperty("evidenceUrls").GetArrayLength());
        Assert.Equal("Evidence", tooMany.Field());
    }

    private static MultipartFormDataContent Photos(int count)
    {
        var form = new MultipartFormDataContent();
        for (var i = 0; i < count; i++)
        {
            var file = new ByteArrayContent(Png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "files", $"evidence{i}.png");
        }

        return form;
    }
}
