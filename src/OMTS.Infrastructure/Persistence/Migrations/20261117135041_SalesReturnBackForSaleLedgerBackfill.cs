using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesReturnBackForSaleLedgerBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A sales return that stands puts the kg of each line taken back for sale on the product ledger (Return rows, at the price of the invoice line), as a return recorded now does. A line is written
            // only when its lot was split into the product, and a return that already has Return rows is left alone. A lot that is Closed now had what was left of it taken off at its close, without these
            // kg, so each product of such a lot gets one more LotClose row for the kg written here: a closed lot still holds no stock.
            migrationBuilder.Sql(@"
DECLARE @added TABLE (ProductId uniqueidentifier NOT NULL, HarvestLotId uniqueidentifier NOT NULL, QuantityKg decimal(18,3) NOT NULL);

INSERT INTO ProductStockMovements (Id, ProductId, HarvestLotId, LotCode, Kind, DocumentId, DocumentCode, OccurredAt, QuantityKg, TransactionPrice, PartyName, PartyCode, RecordedAt, RecordedByAccountId)
OUTPUT inserted.ProductId, inserted.HarvestLotId, inserted.QuantityKg INTO @added
SELECT NEWID(), il.ProductId, rl.HarvestLotId, l.LotCode, 'Return', r.Id, r.Code, r.CreatedAt, rl.ReturnedKg,
       CAST(CASE WHEN il.Quantity = il.Kg THEN il.UnitPrice ELSE ROUND(il.Amount / il.Kg, 0) END AS decimal(18,0)),
       LEFT(c.Name, 200), LEFT(c.Code, 50), SYSDATETIMEOFFSET(), r.RecordedByAccountId
FROM SalesReturnLines rl
JOIN SalesReturns r ON r.Id = rl.ReturnId
JOIN SalesInvoices i ON i.Id = r.InvoiceId
JOIN SalesInvoiceLines il ON il.Id = rl.InvoiceLineId
JOIN HarvestLots l ON l.Id = rl.HarvestLotId
LEFT JOIN Customers c ON c.Id = i.CustomerId
WHERE r.StatusId = 4101
  AND rl.Outcome = 'BackForSale'
  AND EXISTS (SELECT 1 FROM HarvestLotProductLines p WHERE p.LotId = rl.HarvestLotId AND p.ProductId = il.ProductId)
  AND NOT EXISTS (SELECT 1 FROM ProductStockMovements m WHERE m.DocumentId = r.Id AND m.Kind = 'Return');

INSERT INTO ProductStockMovements (Id, ProductId, HarvestLotId, LotCode, Kind, DocumentId, DocumentCode, OccurredAt, QuantityKg, PartyName, RecordedAt)
SELECT NEWID(), a.ProductId, l.Id, l.LotCode, 'LotClose', l.Id, l.LotCode,
       COALESCE(TODATETIMEOFFSET(DATEADD(second, -1, CAST(DATEADD(day, 1, l.CloseDate) AS datetime2)), '+07:00'), SYSDATETIMEOFFSET()),
       -SUM(a.QuantityKg), N'Sales return backfill', SYSDATETIMEOFFSET()
FROM @added a
JOIN HarvestLots l ON l.Id = a.HarvestLotId
WHERE l.StatusId = 2903
GROUP BY a.ProductId, l.Id, l.LotCode, l.CloseDate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The Return rows and the close rows written for them go, with any reversal that undid them.
            migrationBuilder.Sql(@"
DELETE FROM ProductStockMovements WHERE ReversesMovementId IN (SELECT Id FROM ProductStockMovements WHERE Kind = 'Return' OR (Kind = 'LotClose' AND PartyName = N'Sales return backfill'));
DELETE FROM ProductStockMovements WHERE Kind = 'Return' OR (Kind = 'LotClose' AND PartyName = N'Sales return backfill');");
        }
    }
}
