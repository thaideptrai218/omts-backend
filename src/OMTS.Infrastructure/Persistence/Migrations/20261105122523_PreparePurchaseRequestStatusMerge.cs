using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OMTS.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OmtsDbContext))]
[Migration("20261105122523_PreparePurchaseRequestStatusMerge")]
public sealed class PreparePurchaseRequestStatusMerge : EmployeesV2
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- 1901/1902 are now the purchase-return statuses on develop. Move only the old cage-request rows first.
        DECLARE @identity bit = CASE WHEN COLUMNPROPERTY(OBJECT_ID(N'dbo.Statuses'), 'Id', 'IsIdentity') = 1 THEN 1 ELSE 0 END;
        IF @identity = 1 SET IDENTITY_INSERT [Statuses] ON;
        INSERT INTO [Statuses] ([Id],[Code],[DisplayColor],[DisplayName],[Group],[IsFinal],[IsInitial],[Meaning],[SortOrder])
        SELECT s.[Id]+2000,s.[Code],s.[DisplayColor],s.[DisplayName],N'LEGACY_CAGE_PURCHASE_REQUEST',s.[IsFinal],s.[IsInitial],s.[Meaning],s.[SortOrder]
        FROM [Statuses] s WHERE s.[Id] BETWEEN 1901 AND 1905 AND s.[Group] IN (N'PURCHASE_REQUEST',N'LEGACY_CAGE_PURCHASE_REQUEST')
          AND NOT EXISTS (SELECT 1 FROM [Statuses] n WHERE n.[Id] = s.[Id]+2000);
        IF @identity = 1 SET IDENTITY_INSERT [Statuses] OFF;
        IF OBJECT_ID(N'dbo.PurchaseRequest',N'U') IS NOT NULL
            UPDATE r SET [StatusId] = r.[StatusId]+2000 FROM [PurchaseRequest] r JOIN [Statuses] s ON s.[Id] = r.[StatusId]
            WHERE s.[Id] BETWEEN 1901 AND 1905 AND s.[Group] IN (N'PURCHASE_REQUEST',N'LEGACY_CAGE_PURCHASE_REQUEST');
        UPDATE t SET [FromStatusId] = t.[FromStatusId]+2000 FROM [StatusTransitions] t JOIN [Statuses] s ON s.[Id] = t.[FromStatusId]
            WHERE s.[Id] BETWEEN 1901 AND 1905 AND s.[Group] IN (N'PURCHASE_REQUEST',N'LEGACY_CAGE_PURCHASE_REQUEST');
        UPDATE t SET [ToStatusId] = t.[ToStatusId]+2000 FROM [StatusTransitions] t JOIN [Statuses] s ON s.[Id] = t.[ToStatusId]
            WHERE s.[Id] BETWEEN 1901 AND 1905 AND s.[Group] IN (N'PURCHASE_REQUEST',N'LEGACY_CAGE_PURCHASE_REQUEST');
        DELETE FROM [Statuses] WHERE [Id] BETWEEN 1901 AND 1905 AND [Group] IN (N'PURCHASE_REQUEST',N'LEGACY_CAGE_PURCHASE_REQUEST');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
