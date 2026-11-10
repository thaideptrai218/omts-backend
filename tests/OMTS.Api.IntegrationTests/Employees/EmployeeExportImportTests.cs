using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>EMP-API-03 to EMP-API-05: the employee export (filters, selected rows, columns by right), the import template and the import of a file.</summary>
public class EmployeeExportImportTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Url = "/api/employees";

    private static readonly string[] ImportHeaders =
        ["Họ và tên", "Số điện thoại", "Ngày sinh", "Loại nhân viên", "Vùng làm việc", "Loại công việc", "Địa chỉ", "Khu vực", "Phường/Xã", "Ghi chú", "Ngân hàng", "Số tài khoản", "Chủ tài khoản"];

    private static MultipartFormDataContent Upload(byte[] bytes, string name = "employees.xlsx")
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", name);
        return form;
    }

    private static byte[] Workbook(string[] headers, params object?[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Nhân viên");
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = sheet.Cell(r + 2, c + 1);
                switch (rows[r][c])
                {
                    case double number:
                        cell.Value = number;
                        break;
                    case DateTime date:
                        cell.Value = date;
                        break;
                    case string text when text.StartsWith("FORMULA:", StringComparison.Ordinal):
                        cell.FormulaA1 = text["FORMULA:".Length..];
                        break;
                    case string text:
                        cell.Value = text;
                        break;
                }
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>The sheet of an .xlsx answer as header → values of the rows.</summary>
    private static (List<string> Headers, List<Dictionary<string, string>> Rows) Sheet(byte[] file)
    {
        using var workbook = new XLWorkbook(new MemoryStream(file));
        var sheet = workbook.Worksheet(1);
        var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var rows = sheet.RowsUsed().Skip(1)
            .Select(r => headers.Select((h, i) => (h, v: r.Cell(i + 1).GetString())).ToDictionary(x => x.h, x => x.v))
            .ToList();
        return (headers, rows);
    }

    private static async Task<byte[]> BytesAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    // ---- export

    [Fact]
    public async Task The_owner_exports_the_filtered_and_sorted_list_with_personal_columns_and_never_the_bank_or_id_number()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var tag = NewCode();
        var job = await AddJobTypeAsync(factory);
        await CreateAsync(owner, Profile($"{tag} B", NewPhone(), zone.Id, b =>
        {
            b["employeeType"] = "Staff";
            b["jobTypeIds"] = new[] { job.Id };
            b["address"] = "Số 1 Đường A";
            b["idNumber"] = "079090001234";
            b["bankAccount"] = Bank("999900004567");
            b["gender"] = "Male";
        }));
        await CreateAsync(owner, Profile($"{tag} A", null, zone.Id));

        var response = await owner.GetAsync($"{Url}/export?search={tag}&sortBy=name&sortDir=desc");
        var file = await BytesAsync(response);
        var (headers, rows) = Sheet(file);

        Assert.Equal($"employees-{EmployeeRequests.Today():yyyyMMdd}.xlsx", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(
            ["Mã nhân viên", "Họ và tên", "Số điện thoại", "Loại nhân viên", "Loại công việc", "Vùng làm việc", "Tình trạng", "Ngày làm cuối",
             "Ngày sinh", "Giới tính", "Email", "Ngày bắt đầu làm việc", "Địa chỉ", "Khu vực", "Phường/Xã", "Ngân hàng", "Chủ tài khoản"], headers);
        Assert.Equal([$"{tag} B", $"{tag} A"], rows.Select(r => r["Họ và tên"]));
        var b = rows[0];
        Assert.Equal(("Nhân viên", job.Name, zone.Code, "Đang làm việc", "Nam", "Số 1 Đường A", "Vietcombank", "NGUYEN VAN A"),
            (b["Loại nhân viên"], b["Loại công việc"], b["Vùng làm việc"], b["Tình trạng"], b["Giới tính"], b["Địa chỉ"], b["Ngân hàng"], b["Chủ tài khoản"]));
        Assert.Matches(@"^NV\d{6,}$", b["Mã nhân viên"]);
        Assert.DoesNotContain("999900004567", ExtractStrings(file));
        Assert.DoesNotContain("079090001234", ExtractStrings(file));
    }

    /// <summary>Every cell text of the workbook, to show that a value is nowhere in the file (an .xlsx is zipped, so the raw bytes say nothing).</summary>
    private static string ExtractStrings(byte[] file)
    {
        using var workbook = new XLWorkbook(new MemoryStream(file));
        return string.Join('\n', workbook.Worksheets.SelectMany(w => w.CellsUsed()).Select(c => c.GetString()));
    }

    [Fact]
    public async Task The_export_filters_like_the_list_by_type_and_job_type()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var tag = NewCode();
        var job = await AddJobTypeAsync(factory);
        await CreateAsync(owner, Profile($"{tag} S", null, zone.Id, b => b["employeeType"] = "Staff"));
        await CreateAsync(owner, Profile($"{tag} W", null, zone.Id, b => b["jobTypeIds"] = new[] { job.Id }));

        var staff = Sheet(await BytesAsync(await owner.GetAsync($"{Url}/export?search={tag}&employeeType=Staff"))).Rows;
        var byJob = Sheet(await BytesAsync(await owner.GetAsync($"{Url}/export?search={tag}&jobTypeId={job.Id}"))).Rows;

        Assert.Equal([$"{tag} S"], staff.Select(r => r["Họ và tên"]));
        Assert.Equal([$"{tag} W"], byJob.Select(r => r["Họ và tên"]));
    }

    [Fact]
    public async Task With_ids_only_those_employees_are_exported_and_the_other_filters_are_ignored()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var tag = NewCode();
        var a = IdOf(await CreateAsync(owner, Profile($"{tag} A", null, zone.Id)));
        var b = IdOf(await CreateAsync(owner, Profile($"{tag} B", null, zone.Id)));
        await CreateAsync(owner, Profile($"{tag} C", null, zone.Id));

        var rows = Sheet(await BytesAsync(await owner.GetAsync($"{Url}/export?ids={b}&ids={a}&search=nothing-matches&employeeType=Staff&sortBy=name"))).Rows;

        Assert.Equal([$"{tag} A", $"{tag} B"], rows.Select(r => r["Họ và tên"]));
    }

    [Fact]
    public async Task More_than_200_ids_or_a_bad_id_or_sort_is_refused()
    {
        var (owner, _, _) = await SetupAsync(factory);
        var many = string.Join('&', Enumerable.Range(0, 201).Select(_ => $"ids={Guid.NewGuid()}"));

        Assert.Equal("Ids", await (await owner.GetAsync($"{Url}/export?{many}")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await owner.GetAsync($"{Url}/export?ids=nope")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Sort by", await (await owner.GetAsync($"{Url}/export?sortBy=salary")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{Url}/export?{string.Join('&', Enumerable.Range(0, 200).Select(_ => $"ids={Guid.NewGuid()}"))}")).StatusCode);
    }

    [Fact]
    public async Task A_zone_manager_and_an_accountant_cannot_export_or_import_and_an_anonymous_caller_is_unauthorized()
    {
        var (_, zone, _) = await SetupAsync(factory);
        var manager = await ManagerAsync(factory, zone);
        var accountant = await ManagerAsync(factory, null, StaffRole.Accountant);
        var template = Workbook(ImportHeaders);

        foreach (var client in new[] { manager, accountant })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}/export")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}/import/template")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Url}/import", Upload(template))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{Url}/export")).StatusCode);
    }

    // ---- template and import

    [Fact]
    public async Task The_template_has_the_import_columns_and_no_data_rows()
    {
        var (owner, _, _) = await SetupAsync(factory);

        var response = await owner.GetAsync($"{Url}/import/template");
        var (headers, rows) = Sheet(await BytesAsync(response));

        Assert.Equal("employees-import-template.xlsx", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(ImportHeaders, headers.Take(13));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task A_valid_file_imports_every_row_by_zone_code_and_job_type_name()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var second = await factory.AddZoneAsync(NewCode());
        var job1 = await AddJobTypeAsync(factory);
        var job2 = await AddJobTypeAsync(factory);
        var tag = NewCode();
        var phone = NewPhone();
        var file = Workbook(ImportHeaders.Concat(["Mã nhân viên", "Giới tính", "Số CCCD", "Email", "Ngày bắt đầu làm việc"]).ToArray(),
            [$"{tag} Một", phone, "14/03/1988", "Thời vụ", $"{zone.Code}, {second.Code}", $"{job1.Name}, {job2.Name.ToLowerInvariant()}", "Số 1", "Vân Đồn", "Cái Rồng", "Ghi chú", "Vietcombank", "999900007777", "Nguyen Van A", "imp-1", "Nữ", "079090001234", "A@Example.com", "2026-01-05"],
            [$"{tag} Hai", double.Parse(NewPhone()[1..]), null, "Staff", zone.Code.ToLowerInvariant(), null, null, null, null, null, null, null, null],
            [$"{tag} Ba", null, new DateTime(1990, 5, 1), "nhan vien", zone.Code]);

        var result = await (await owner.PostAsync($"{Url}/import", Upload(file))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(3, result.GetProperty("imported").GetInt32());
        Assert.Empty(result.GetProperty("errors").EnumerateArray());
        var saved = await factory.QueryAsync(db => db.Employees
            .Include(e => e.WorkingZones).Include(e => e.JobTypes).Include(e => e.TypePeriods)
            .Where(e => e.FullName.StartsWith(tag)).OrderBy(e => e.FullName).ToListAsync());
        var (one, two, three) = (saved.Single(e => e.FullName.EndsWith("Một")), saved.Single(e => e.FullName.EndsWith("Hai")), saved.Single(e => e.FullName.EndsWith("Ba")));
        Assert.Equal(("IMP-1", phone, new DateOnly(1988, 3, 14), Gender.Female, "079090001234", "a@example.com", new DateOnly(2026, 1, 5)),
            (one.Code, one.PhoneNumber, one.Birthday, one.Gender, one.IdNumber, one.Email, one.StartDate));
        Assert.Equal(new[] { zone.Id, second.Id }.Order(), one.WorkingZones.Select(w => w.ZoneId).Order());
        Assert.Equal(new[] { job1.Id, job2.Id }.Order(), one.JobTypes.Select(j => j.JobTypeId).Order());
        Assert.Equal("999900007777", one.BankAccount!.AccountNumber);
        Assert.Equal(EmployeeType.SeasonalWorker, one.TypePeriods.Single().Type);
        // A phone typed in a number cell lost its leading zero; the date cell is read as the date it holds.
        Assert.StartsWith("0", two.PhoneNumber);
        Assert.Equal(EmployeeType.Staff, two.TypePeriods.Single().Type);
        Assert.Equal(new DateOnly(1990, 5, 1), three.Birthday);
        Assert.Matches(@"^NV\d{6,}$", two.Code);
        Assert.Matches(@"^NV\d{6,}$", three.Code);
        Assert.NotEqual(two.Code, three.Code);
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == one.Id && e.Action == "Employee.Created"));
        Assert.Equal("Imported from a file.", audit.Reason);
    }

    [Fact]
    public async Task Rows_with_errors_are_skipped_and_reported_and_the_good_rows_are_imported()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var job = await AddJobTypeAsync(factory);
        var discontinued = await AddJobTypeAsync(factory, j => j.StatusId = OMTS.Domain.Constants.StatusIds.Usage.Discontinued);
        var taken = NewPhone();
        var existing = await CreateAsync(owner, Profile($"Existing {NewCode()}", taken, zone.Id));
        var tag = NewCode();
        var repeated = NewPhone();
        var good = NewPhone();
        var file = Workbook(ImportHeaders,
            [$"{tag} tốt", good, null, "Thời vụ", zone.Code, job.Name],                                  // row 2: good
            [null, NewPhone(), null, "Thời vụ", zone.Code],                                                 // row 3: no name
            [$"{tag} trùng", taken, null, "Thời vụ", zone.Code],                                             // row 4: phone of an Employed employee
            [$"{tag} vùng", NewPhone(), null, "Thời vụ", "NO-SUCH-ZONE"],                                  // row 5: unknown zone code
            [$"{tag} việc", NewPhone(), null, "Thời vụ", zone.Code, "No such job type"],                   // row 6: unknown job type
            [$"{tag} loại", NewPhone(), null, "Cộng tác viên", zone.Code],                                 // row 7: unknown type
            [$"{tag} điện thoại", "123", null, "Thời vụ", zone.Code],                                      // row 8: bad phone
            [$"{tag} lặp 1", repeated, null, "Thời vụ", zone.Code],                                        // row 9: good
            [$"{tag} lặp 2", repeated, null, "Thời vụ", zone.Code],                                        // row 10: phone repeated in the file
            [$"{tag} ngân hàng", NewPhone(), null, "Thời vụ", zone.Code, null, null, null, null, null, "Vietcombank", "1", "X"], // row 11: bank
            [$"{tag} công cụ", NewPhone(), null, "Thời vụ", zone.Code, discontinued.Name],                 // row 12: discontinued job type
            [$"{tag} công thức", "FORMULA:1+1", null, "Thời vụ", zone.Code],                               // row 13: formula
            [$"{tag} ngày sinh", NewPhone(), "31/02/1990", "Thời vụ", zone.Code]);                         // row 14: not a date

        var result = await (await owner.PostAsync($"{Url}/import", Upload(file))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2, result.GetProperty("imported").GetInt32());
        var errors = result.GetProperty("errors").EnumerateArray().ToDictionary(e => e.GetProperty("row").GetInt32());
        Assert.Equal([3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14], errors.Keys.Order());
        Assert.Equal(["Full name"], Fields(errors[3]));
        Assert.Equal(["Phone number"], Fields(errors[4]));
        Assert.Equal("MSG-LABOR-03", errors[4].GetProperty("messageCode").GetString());
        Assert.Contains(existing.GetProperty("employee").GetProperty("fullName").GetString()!, errors[4].GetProperty("message").GetString());
        Assert.Contains("Working zones", Fields(errors[5]));
        Assert.Contains("Job types", Fields(errors[6]));
        Assert.Contains("Employee type", Fields(errors[7]));
        Assert.Equal(["Phone number"], Fields(errors[8]));
        Assert.Equal(["Phone number"], Fields(errors[10]));
        Assert.Equal("Repeated in this file.", errors[10].GetProperty("message").GetString());
        Assert.Equal(["Bank account"], Fields(errors[11]));
        Assert.Equal(["Job types"], Fields(errors[12]));
        Assert.Empty(Fields(errors[13]));
        Assert.Equal("Formulas are not accepted.", errors[13].GetProperty("message").GetString());
        Assert.Equal(["Birthday"], Fields(errors[14]));
        var names = await factory.QueryAsync(db => db.Employees.Where(e => e.FullName.StartsWith(tag)).Select(e => e.FullName).OrderBy(n => n).ToListAsync());
        Assert.Equal([$"{tag} lặp 1", $"{tag} tốt"], names);
    }

    private static string[] Fields(JsonElement error) => error.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray();

    [Fact]
    public async Task A_file_that_is_not_usable_is_refused_as_a_whole()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        async Task<string?> Refused(byte[] bytes) => await (await owner.PostAsync($"{Url}/import", Upload(bytes))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        Assert.Equal("File", await Refused("not a workbook"u8.ToArray()));
        Assert.Equal("File", await Refused(new byte[2 * 1024 * 1024 + 1]));
        Assert.Equal("File", await Refused(Workbook(["Other"], ["x"])));
        Assert.Equal("File", await Refused(Workbook(ImportHeaders)));
        Assert.Equal("File", await Refused(Workbook(["Họ và tên", "Số điện thoại"], ["A", "0912345678"])));
        var many = Workbook(ImportHeaders, Enumerable.Range(0, 1001).Select(i => new object?[] { $"E{i}", null, null, "Thời vụ", zone.Code }).ToArray());
        Assert.Equal("File", await Refused(many));
    }

    [Fact]
    public async Task A_file_of_exactly_1000_rows_is_accepted()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var tag = NewCode();
        var file = Workbook(ImportHeaders, Enumerable.Range(0, 1000).Select(i => new object?[] { $"{tag} {i}", null, null, "Thời vụ", zone.Code }).ToArray());

        var result = await (await owner.PostAsync($"{Url}/import", Upload(file))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(1000, result.GetProperty("imported").GetInt32());
        var codes = await factory.QueryAsync(db => db.Employees.Where(e => e.FullName.StartsWith(tag)).Select(e => e.Code).ToListAsync());
        Assert.Equal(1000, codes.Distinct().Count());
    }

    [Fact]
    public async Task What_the_export_writes_the_import_reads_back()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var job = await AddJobTypeAsync(factory);
        var tag = NewCode();
        var phone = NewPhone();
        await CreateAsync(owner, Profile($"{tag} vòng", phone, zone.Id, b => { b["jobTypeIds"] = new[] { job.Id }; b["gender"] = "Female"; b["code"] = "ROUND-1"; }));
        var exported = await BytesAsync(await owner.GetAsync($"{Url}/export?search={tag}"));
        // The same person under another phone number and code: the export columns are the import columns.
        using var workbook = new XLWorkbook(new MemoryStream(exported));
        var sheet = workbook.Worksheet(1);
        var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        sheet.Cell(2, headers.IndexOf("Số điện thoại") + 1).Value = NewPhone();
        sheet.Cell(2, headers.IndexOf("Mã nhân viên") + 1).Value = "ROUND-2";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var result = await (await owner.PostAsync($"{Url}/import", Upload(stream.ToArray()))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(1, result.GetProperty("imported").GetInt32());
        var copy = await factory.QueryAsync(db => db.Employees.Include(e => e.JobTypes).SingleAsync(e => e.Code == "ROUND-2"));
        Assert.Equal(("Female", job.Id), (copy.Gender.ToString(), copy.JobTypes.Single().JobTypeId));
    }
}
