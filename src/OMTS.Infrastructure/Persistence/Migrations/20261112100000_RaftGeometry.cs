using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Numeric geometry of a raft (decision of 12/11/2026): length and width in metres (the area is worked out in code), and an optional line length per lane. The legacy text
    /// <c>Rafts.Size</c> stays and is kept in step by the server. Existing rafts are backfilled from the text, but only when it is exactly "{number} m x {number} m" (the form the web writes:
    /// length x width, a dot as decimal point, at most 2 decimals) and the numbers are in range (length 1 to 500, width 1 to 200); anything else is left null and the text untouched,
    /// never guessed. Down drops the columns: the size text is unchanged, so nothing but the numbers is lost.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261112100000_RaftGeometry")]
    public partial class RaftGeometry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(name: "LengthM", table: "Rafts", type: "decimal(7,2)", precision: 7, scale: 2, nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "WidthM", table: "Rafts", type: "decimal(7,2)", precision: 7, scale: 2, nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "LineLengthM", table: "Lanes", type: "decimal(7,2)", precision: 7, scale: 2, nullable: true);

            // The checks come after the backfill, which only writes values inside them.
            migrationBuilder.Sql(@"
                UPDATE r SET r.LengthM = CAST(p.L AS decimal(7,2)), r.WidthM = CAST(p.W AS decimal(7,2))
                FROM Rafts r
                CROSS APPLY (SELECT CHARINDEX(' m x ', r.Size) AS Pos) x
                CROSS APPLY (SELECT
                        LEFT(r.Size, CASE WHEN x.Pos > 1 THEN x.Pos - 1 ELSE 0 END) AS LText,
                        SUBSTRING(r.Size, x.Pos + 5, CASE WHEN LEN(r.Size) - x.Pos - 6 > 0 THEN LEN(r.Size) - x.Pos - 6 ELSE 0 END) AS WText) t
                CROSS APPLY (SELECT TRY_CAST(NULLIF(t.LText, '') AS decimal(18,6)) AS L, TRY_CAST(NULLIF(t.WText, '') AS decimal(18,6)) AS W) p
                WHERE r.Size IS NOT NULL AND x.Pos > 1 AND RIGHT(r.Size, 2) = ' m'
                  AND t.LText NOT LIKE '%[^0-9.]%' AND t.WText NOT LIKE '%[^0-9.]%'
                  AND p.L BETWEEN 1 AND 500 AND p.W BETWEEN 1 AND 200
                  AND p.L = ROUND(p.L, 2) AND p.W = ROUND(p.W, 2);");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Raft_Geometry", table: "Rafts", sql: "([LengthM] IS NULL OR [LengthM] BETWEEN 1 AND 500) AND ([WidthM] IS NULL OR [WidthM] BETWEEN 1 AND 200)");
            migrationBuilder.AddCheckConstraint(name: "CK_Lane_LineLength", table: "Lanes", sql: "[LineLengthM] IS NULL OR [LineLengthM] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_Raft_Geometry", table: "Rafts");
            migrationBuilder.DropCheckConstraint(name: "CK_Lane_LineLength", table: "Lanes");
            migrationBuilder.DropColumn(name: "LengthM", table: "Rafts");
            migrationBuilder.DropColumn(name: "WidthM", table: "Rafts");
            migrationBuilder.DropColumn(name: "LineLengthM", table: "Lanes");
        }
    }
}
