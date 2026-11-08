using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OMTS.Infrastructure.Persistence.Migrations;

/// <summary>Runs before the shared legacy cleanup, including on an existing Mini App database.</summary>
[DbContext(typeof(OmtsDbContext))]
[Migration("20261028151319_PreserveMiniAppPayrollLocks")]
public sealed class PreserveMiniAppPayrollLocks : AddZaloAccountLinking
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep the old financial records before develop retires their tables. The final integration imports their locks.
        foreach (var name in new[] { "LaborSettlements", "PayLines", "LaborPayments" })
            migrationBuilder.Sql($"IF OBJECT_ID(N'[dbo].[{name}]', N'U') IS NOT NULL AND OBJECT_ID(N'[dbo].[MiniAppLegacy{name}]', N'U') IS NULL " +
                $"EXEC(N'IF EXISTS (SELECT 1 FROM [dbo].[{name}]) SELECT * INTO [dbo].[MiniAppLegacy{name}] FROM [dbo].[{name}]');");

        migrationBuilder.Sql("""
            IF OBJECT_ID(N'[dbo].[StatusTransitions]', N'U') IS NOT NULL
            BEGIN
                DECLARE @identity bit = CASE WHEN COLUMNPROPERTY(OBJECT_ID(N'[dbo].[StatusTransitions]'), 'Id', 'IsIdentity') = 1 THEN 1 ELSE 0 END;
                -- The pair (FromStatusId, ToStatusId) is unique, so the old rows must go before their copies are written.
                SELECT t.[Id] + 30000 AS [Id], t.[AllowedPerformers], t.[FromStatusId], t.[GuardCondition], t.[ReasonRequired], t.[ToStatusId], t.[TriggerEvent], t.[UseCases]
                INTO #MovedTransitions
                FROM [StatusTransitions] t
                WHERE t.[Id] BETWEEN 74 AND 84
                  AND (t.[UseCases] LIKE N'%Zalo%' OR t.[FromStatusId] BETWEEN 1901 AND 1905 OR t.[ToStatusId] BETWEEN 1901 AND 1905)
                  AND NOT EXISTS (SELECT 1 FROM [StatusTransitions] n WHERE n.[Id] = t.[Id] + 30000);
                DELETE FROM [StatusTransitions] WHERE [Id] BETWEEN 74 AND 84
                  AND ([UseCases] LIKE N'%Zalo%' OR [FromStatusId] BETWEEN 1901 AND 1905 OR [ToStatusId] BETWEEN 1901 AND 1905);
                IF @identity = 1 SET IDENTITY_INSERT [StatusTransitions] ON;
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases])
                SELECT [Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases] FROM #MovedTransitions;
                IF @identity = 1 SET IDENTITY_INSERT [StatusTransitions] OFF;
                DROP TABLE #MovedTransitions;
            END;
            """);
    }

    // Financial archives are retained across rollback.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
