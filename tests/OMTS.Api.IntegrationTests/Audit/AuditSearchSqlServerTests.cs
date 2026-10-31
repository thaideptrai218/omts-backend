using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>ACCOUNT-10 on SQL Server: the schema of the AuditSearch migration and the search as SQL Server runs it.</summary>
public class AuditSearchSqlServerTests
{
    // Each filter leads an index that ends with the time; the old index of the account foreign key is replaced.
    private static readonly Dictionary<string, string> ExpectedIndexes = new()
    {
        ["IX_AuditEvents_OccurredAt"] = "OccurredAt",
        ["IX_AuditEvents_ZoneId_OccurredAt"] = "ZoneId,OccurredAt",
        ["IX_AuditEvents_ActorAccountId_OccurredAt"] = "ActorAccountId,OccurredAt",
        ["IX_AuditEvents_ActorEmployeeId_OccurredAt"] = "ActorEmployeeId,OccurredAt",
        ["IX_AuditEvents_Action_OccurredAt"] = "Action,OccurredAt",
        ["IX_AuditEvents_RecordCode_OccurredAt"] = "RecordCode,OccurredAt",
        ["IX_AuditEvents_RecordType_RecordId"] = "RecordType,RecordId",
    };

    [SqlServerFact]
    public async Task The_migration_adds_the_employee_actor_and_the_search_indexes()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();

        var indexes = await db.Database.SqlQueryRaw<IndexRow>("""
            SELECT i.name AS Name, STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS Columns
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('AuditEvents') AND i.is_primary_key = 0
            GROUP BY i.name
            """).ToDictionaryAsync(i => i.Name, i => i.Columns);
        var columns = await db.Database.SqlQueryRaw<ColumnRow>("""
            SELECT c.name AS Name, c.is_nullable AS Nullable, t.name AS Type
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('AuditEvents')
            """).ToDictionaryAsync(c => c.Name);
        var foreignKeys = await db.Database.SqlQueryRaw<string>("""
            SELECT c.name AS Value FROM sys.foreign_key_columns f
            JOIN sys.columns c ON c.object_id = f.parent_object_id AND c.column_id = f.parent_column_id
            WHERE f.parent_object_id = OBJECT_ID('AuditEvents')
            """).ToListAsync();

        Assert.Equal(ExpectedIndexes.OrderBy(i => i.Key), indexes.OrderBy(i => i.Key));
        Assert.Equal(("uniqueidentifier", true), (columns["ActorEmployeeId"].Type, columns["ActorEmployeeId"].Nullable));
        Assert.False(columns.ContainsKey("ViewScope"));
        // The employee has no foreign key yet (LABOR adds it).
        Assert.Equal(["ActorAccountId"], foreignKeys);
    }

    [SqlServerFact]
    public async Task Filters_paging_facets_details_and_records_work_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync();
        }

        await using var api = new SqlServerApiFactory(database);
        var password = NewPassword();
        var owner = await api.AddAccountAsync(NewCode(), password);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await LoginTokensAsync(client, owner.Username, password)).GetProperty("accessToken").GetString());

        var tag = NewTag();
        var now = DateTimeOffset.UtcNow;
        Guid zoneId, employeeId, licenseId;
        AuditEvent zoneEvent, licenseEvent, employeeEvent;
        await using (var db = database.CreateContext())
        {
            var cooperative = new Cooperative { FullName = "HTX Trung Nam" };
            var zone = new FarmingZone { Code = $"{tag}Z", Name = "Vùng A", Cooperative = cooperative };
            var employee = new Employee { FullName = "Phạm Văn Công", Cooperative = cooperative };
            var license = new ZoneComplianceDocument { DocumentType = "AQUACULTURE_LICENSE", DocumentNumber = $"{tag}L" };
            db.AddRange(zone, employee, license);
            await db.SaveChangesAsync();
            (zoneId, employeeId, licenseId) = (zone.Id, employee.Id, license.Id);

            zoneEvent = With(Event($"{tag}Z", now.AddMinutes(-5), "FarmingZone.Updated", nameof(FarmingZone)), owner.Id, null, zoneId);
            zoneEvent.RecordId = zoneId;
            zoneEvent.Before = """{"Name":"Vùng cũ"}""";
            zoneEvent.After = """{"Name":"Vùng A"}""";
            licenseEvent = With(Event($"{tag}L", now.AddMinutes(-4), "ZoneLicense.Created", "ZoneLicense"), owner.Id, null, zoneId);
            licenseEvent.RecordId = licenseId;
            employeeEvent = With(Event($"{tag}_E", now.AddMinutes(-3), "Attendance.Recorded", "Attendance"), null, employeeId, zoneId);
            db.AuditEvents.AddRange(
                zoneEvent, licenseEvent, employeeEvent,
                // Wildcards in a code: "_" and "%" are plain characters in the record code filter.
                Event($"{tag}AE", now.AddMinutes(-2), "Job.Ran", "Job"),
                Event($"{tag}%X", now.AddMinutes(-1), "Job.Ran", "Job"),
                Event($"{tag}-OLD", now.AddDays(-45), "Job.Ran", "Job"));
            await db.SaveChangesAsync();
        }

        Assert.Equal([$"{tag}%X", $"{tag}AE", $"{tag}_E", $"{tag}L", $"{tag}Z"], Codes(await client.SearchAsync($"recordCode={tag}")));
        Assert.Equal([$"{tag}_E"], Codes(await client.SearchAsync($"recordCode={Uri.EscapeDataString($"{tag}_")}")));
        Assert.Equal([$"{tag}%X"], Codes(await client.SearchAsync($"recordCode={Uri.EscapeDataString($"{tag}%")}")));
        Assert.Equal([$"{tag}L", $"{tag}Z"], Codes(await client.SearchAsync($"actorAccountId={owner.Id}&recordCode={tag}")));
        Assert.Equal([$"{tag}%X", $"{tag}AE"], Codes(await client.SearchAsync($"system=true&action=Job.Ran&recordCode={tag}")));
        Assert.Equal([$"{tag}%X", $"{tag}AE", $"{tag}-OLD"], Codes(await client.SearchAsync($"system=true&action=Job.Ran&recordCode={tag}&from={Date(Today.AddDays(-60))}")));
        Assert.Equal([$"{tag}_E", $"{tag}L"], Codes(await client.SearchAsync($"zoneId={zoneId}&pageSize=2")));
        Assert.Equal([$"{tag}Z"], Codes(await client.SearchAsync($"zoneId={zoneId}&pageSize=2&page=2")));
        Assert.Equal([$"{tag}Z"], Codes(await client.SearchAsync($"recordType=FarmingZone&action=FarmingZone.Updated&recordCode={tag}")));
        var byEmployee = await client.SearchAsync($"actorEmployeeId={employeeId}");
        Assert.Equal("Phạm Văn Công", byEmployee.GetProperty("page").GetProperty("items")[0].GetProperty("actor").GetProperty("name").GetString());
        Assert.Equal("MSG-GEN-10", (await client.SearchAsync($"recordCode={tag}&recordType=Nothing")).GetProperty("messageCode").GetString());

        var facets = await client.GetFromJsonAsync<JsonElement>("/api/audit-events/facets");
        Assert.Contains("Attendance.Recorded", facets.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("ZoneLicense", facets.GetProperty("recordTypes").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(2, facets.GetProperty("people").GetArrayLength());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/audit-events/{zoneEvent.Id}");
        Assert.Equal(("Vùng cũ", "Vùng A"), (detail.GetProperty("before").GetProperty("Name").GetString(), detail.GetProperty("after").GetProperty("Name").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/audit-events/{zoneEvent.Id}/record")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/audit-events/{licenseEvent.Id}/record")).StatusCode);
        Assert.Equal("MSG-ACCOUNT-22", await (await client.GetAsync($"/api/audit-events/{employeeEvent.Id}/record")).ErrorCodeAsync());

        await using (var db = database.CreateContext())
        {
            db.ZoneComplianceDocuments.Remove(await db.ZoneComplianceDocuments.SingleAsync(d => d.Id == licenseId));
            await db.SaveChangesAsync();
        }

        var gone = await client.GetAsync($"/api/audit-events/{licenseEvent.Id}/record");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("MSG-ACCOUNT-22", await gone.ErrorCodeAsync());
    }

    // Notifications and audit events name zone licenses the same way; the audit log is never edited, so the notifications move.
    [SqlServerFact]
    public async Task The_migration_names_the_zone_licenses_of_stored_notifications_as_the_audit_log_does()
    {
        const string notificationsV2 = "20261031010823_NotificationsV2";
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(notificationsV2);
        var owner = new Account { Username = NewCode(), DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner };
        db.Notifications.AddRange(
            new Notification { RecipientAccount = owner, Type = "ZONE_LICENSE_EXPIRY", Title = "License", RelatedRecordType = "ZoneComplianceDocument", RelatedRecordId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow },
            new Notification { RecipientAccount = owner, Type = "WATER_ALERT", Title = "Water", RelatedRecordType = "WaterMeasurement", RelatedRecordId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();
        Assert.Equal(["WaterMeasurement", "ZoneLicense"], await RelatedRecordTypesAsync(db));

        await migrator.MigrateAsync(notificationsV2);
        Assert.Equal(["WaterMeasurement", "ZoneComplianceDocument"], await RelatedRecordTypesAsync(db));
    }

    private static Task<List<string?>> RelatedRecordTypesAsync(OmtsDbContext db) =>
        db.Notifications.AsNoTracking().Select(n => n.RelatedRecordType).OrderBy(t => t).ToListAsync();

    private sealed record IndexRow(string Name, string Columns);

    private sealed record ColumnRow(string Name, bool Nullable, string Type);
}
