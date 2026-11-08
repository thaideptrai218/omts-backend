using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OMTS.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OmtsDbContext))]
[Migration("20261104174336_PreserveMiniAppCatalogueMetadata")]
public sealed class PreserveMiniAppCatalogueMetadata : AccountRulesV2
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        IF COL_LENGTH(N'dbo.Products', N'Code') IS NOT NULL AND OBJECT_ID(N'[dbo].[MiniAppLegacyProductMetadata]', N'U') IS NULL
            EXEC(N'SELECT [Id], [Code], [Description], [SalesUnit], [ValidFrom], [ValidTo] INTO [dbo].[MiniAppLegacyProductMetadata] FROM [dbo].[Products]');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
