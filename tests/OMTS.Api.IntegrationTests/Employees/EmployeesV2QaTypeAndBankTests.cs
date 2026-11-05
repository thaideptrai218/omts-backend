using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>QA of OMTS-227, the employee type history (BR-LABOR-01, LABOR-01.1) and the bank block with its audit (GBR-02).</summary>
public class EmployeesV2QaTypeAndBankTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static List<(string? Type, string? From)> History(JsonElement view) =>
        view.GetProperty("typeHistory").EnumerateArray().Select(p => (p.GetProperty("type").GetString(), p.GetProperty("effectiveFrom").GetString())).ToList();

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd");

    [Fact]
    public async Task A_new_type_keeps_the_earlier_ones_and_the_same_date_replaces_with_the_old_one_in_the_audit()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Type {NewCode()}", NewPhone());
        var today = Today();
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id).With("employeeTypeFrom", Day(today.AddDays(-30))));
        var id = IdOf(saved);
        Assert.Equal("SeasonalWorker", saved.GetProperty("employee").GetProperty("employeeType").GetString());
        Assert.Contains("SeasonalWorker", await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id && e.Action == "Employee.Created").Select(e => e.After).SingleAsync()));

        // A date in the past: applies now, the first stays.
        var past = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("employeeType", "Staff").With("employeeTypeFrom", Day(today.AddDays(-5))).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Staff", past.GetProperty("employee").GetProperty("employeeType").GetString());
        Assert.Equal([("SeasonalWorker", Day(today.AddDays(-30))), ("Staff", Day(today.AddDays(-5)))], History(past.GetProperty("employee")));

        // A date in the future: today's type stands until then.
        var future = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("employeeType", "SeasonalWorker").With("employeeTypeFrom", Day(today.AddDays(10))).With("rowVersion", RowVersionOf(past)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Staff", future.GetProperty("employee").GetProperty("employeeType").GetString());
        Assert.Equal(3, History(future.GetProperty("employee")).Count);

        // The same date replaces that period; the audit row keeps the old type.
        var replaced = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("employeeType", "SeasonalWorker").With("employeeTypeFrom", Day(today.AddDays(-5))).With("rowVersion", RowVersionOf(future)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal("SeasonalWorker", replaced.GetProperty("employee").GetProperty("employeeType").GetString());
        Assert.Equal(("SeasonalWorker", Day(today.AddDays(-5))), History(replaced.GetProperty("employee"))[1]);
        var audit = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id && e.Action == "Employee.Updated").OrderBy(e => e.OccurredAt).Select(e => e.Before).ToListAsync());
        Assert.Equal(3, audit.Count);
        Assert.Contains("Staff", audit[2]);

        // The type in force, with or without a date on which it already holds, changes nothing.
        var version = RowVersionOf(replaced);
        var same = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("rowVersion", version))).JsonAsync(HttpStatusCode.OK);
        var dated = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("employeeTypeFrom", Day(today)).With("rowVersion", version))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(version, RowVersionOf(same));
        Assert.Equal(version, RowVersionOf(dated));
    }

    [Fact]
    public async Task A_date_before_the_first_type_adds_an_earlier_period_and_the_current_type_is_not_changed_by_it()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Before {NewCode()}", NewPhone());
        var today = Today();
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));

        var earlier = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id).With("employeeType", "Staff").With("employeeTypeFrom", Day(today.AddDays(-9))).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        Assert.Equal("SeasonalWorker", earlier.GetProperty("employee").GetProperty("employeeType").GetString());
        Assert.Equal([("Staff", Day(today.AddDays(-9))), ("SeasonalWorker", Day(today))], History(earlier.GetProperty("employee")));
    }

    [Fact]
    public void The_type_on_a_day_is_the_latest_period_from_that_day_or_before_and_before_every_period_the_first()
    {
        var first = new DateOnly(2026, 3, 10);
        var periods = new[]
        {
            new EmployeeTypePeriod { Type = EmployeeType.Staff, EffectiveFrom = first.AddDays(20) },
            new EmployeeTypePeriod { Type = EmployeeType.SeasonalWorker, EffectiveFrom = first },
        };

        Assert.Equal(EmployeeType.SeasonalWorker, EmployeeTypePeriod.TypeOn(periods, first.AddDays(-1)));
        Assert.Equal(EmployeeType.SeasonalWorker, EmployeeTypePeriod.TypeOn(periods, first));
        Assert.Equal(EmployeeType.SeasonalWorker, EmployeeTypePeriod.TypeOn(periods, first.AddDays(19)));
        Assert.Equal(EmployeeType.Staff, EmployeeTypePeriod.TypeOn(periods, first.AddDays(20)));
        Assert.Null(EmployeeTypePeriod.TypeOn([], first));
    }

    // BR-LABOR-01: "The employee type is Seasonal worker or Staff". The JSON enum converter also takes any number.
    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task M3_a_number_that_is_no_employee_type_is_refused(int type)
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Number {NewCode()}", NewPhone());

        var added = await owner.PostAsJsonAsync("/api/employees", Profile(name, phone, zone.Id).With("employeeType", type));

        await added.Content.LoadIntoBufferAsync();
        Assert.True(added.StatusCode == HttpStatusCode.BadRequest, $"{(int)added.StatusCode}: {await added.Content.ReadAsStringAsync()}");
        Assert.Equal(("MSG-GEN-01", "Employee type"), (await added.ErrorCodeAsync(), await FieldAsync(added)));
    }

    // The edit reads the same input, and a number that is a type stays a type: the JSON setting is not changed.
    [Fact]
    public async Task M3_an_edit_refuses_it_too_and_the_numbers_of_the_two_types_stay_valid()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Number {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));

        var edited = await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id).With("employeeType", 99).With("employeeTypeFrom", Day(Today())).With("rowVersion", RowVersionOf(saved)));
        var defined = await CreateAsync(owner, Profile($"Number {NewCode()}", NewPhone(), zone.Id).With("employeeType", 1));

        Assert.Equal(("MSG-GEN-01", "Employee type"), (await edited.ErrorCodeAsync(), await FieldAsync(edited)));
        Assert.Equal("Staff", defined.GetProperty("employee").GetProperty("employeeType").GetString());
    }

    [Fact]
    public async Task A_name_that_is_no_employee_type_is_refused_on_add_and_edit()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Name {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/employees", Profile($"N {NewCode()}", NewPhone(), zone.Id).With("employeeType", "Contractor"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id).With("employeeType", "Contractor").With("rowVersion", RowVersionOf(saved)))).StatusCode);
    }

    // GBR-02: each edit is written with the values before and after. The diff compares the masked number.
    [Fact]
    public async Task M1_a_new_bank_number_with_the_same_last_four_digits_is_written_to_the_audit_log()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Bank {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id).With("bankAccount", Bank("999911112222")));
        var id = IdOf(saved);

        var edited = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("bankAccount", Bank("999933332222")).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        Assert.Equal("999933332222", edited.GetProperty("employee").GetProperty("bankAccount").GetProperty("accountNumber").GetString());
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "Employee.Updated")));

        // Both masks read the same: the row says the number changed, and holds neither number.
        var row = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == id && e.Action == "Employee.Updated"));
        Assert.Contains("********2222", row.Before);
        Assert.DoesNotContain("AccountNumberChanged", row.Before);
        Assert.Contains("********2222", row.After);
        Assert.Contains("\"AccountNumberChanged\":true", row.After);
        Assert.DoesNotContain(new[] { row.Before, row.After }, r => r!.Contains("999911112222") || r.Contains("999933332222"));
    }

    // The same marker on a row whose masks differ; a number that stays writes no marker and no row.
    [Fact]
    public async Task M1_the_marker_follows_the_real_number_only()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Bank {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id).With("bankAccount", Bank("999911112222")));
        var id = IdOf(saved);

        var same = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("bankAccount", Bank("999911112222")).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordId == id && e.Action == "Employee.Updated")));

        await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("bankAccount", Bank("999911113333")).With("rowVersion", RowVersionOf(same)))).JsonAsync(HttpStatusCode.OK);
        var after = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id && e.Action == "Employee.Updated").Select(e => e.After).SingleAsync());
        Assert.Contains("********3333", after);
        Assert.Contains("\"AccountNumberChanged\":true", after);
    }

    [Fact]
    public async Task The_bank_block_is_all_three_or_none_and_its_number_is_six_to_twenty_digits()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var invalid = new object[]
        {
            new { bankName = "BIDV", accountNumber = "999900000001" },
            new { bankName = "BIDV", accountHolderName = "A" },
            new { accountNumber = "999900000001", accountHolderName = "A" },
            new { bankName = "BIDV", accountNumber = "12345", accountHolderName = "A" },
            new { bankName = "BIDV", accountNumber = new string('9', 21), accountHolderName = "A" },
            new { bankName = "BIDV", accountNumber = "99990000000A", accountHolderName = "A" },
            new { bankName = "BIDV", accountNumber = InDigits("0123456789", '٠'), accountHolderName = "A" },
        };
        foreach (var bank in invalid)
        {
            var refused = await owner.PostAsJsonAsync("/api/employees", Profile($"Bank {NewCode()}", NewPhone(), zone.Id).With("bankAccount", bank));
            Assert.Equal(("MSG-GEN-01", "Bank account"), (await refused.ErrorCodeAsync(), await FieldAsync(refused)));
        }

        var empty = await CreateAsync(owner, Profile($"Empty {NewCode()}", NewPhone(), zone.Id).With("bankAccount", new { bankName = " ", accountNumber = string.Empty }));
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("employee").GetProperty("bankAccount").ValueKind);
    }

    // The maintainers' screen sends the job types only when it has them (client list §2): what a PUT leaves out.
    [Fact(Skip = "Waiting for the PM: should a PUT that leaves out jobTypeIds keep the job types, or does what is sent replace the list as the client list says?")]
    public async Task L10_an_edit_that_leaves_out_the_job_types_keeps_them()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Jobs {NewCode()}", NewPhone());
        var job = await AddJobTypeAsync(factory);
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id).With("jobTypeIds", new[] { job.Id }));

        var edited = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id).With("notes", "Không gửi loại việc").With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);

        Assert.Equal(1, edited.GetProperty("employee").GetProperty("jobTypes").GetArrayLength());
    }
}
