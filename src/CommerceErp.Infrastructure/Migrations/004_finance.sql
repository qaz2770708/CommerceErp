CREATE TABLE BillBatches(Id TEXT PRIMARY KEY, TenantId TEXT NOT NULL, Platform TEXT NOT NULL, ShopId TEXT NOT NULL,
 Number TEXT NOT NULL, FileName TEXT NOT NULL, ContentHash TEXT NOT NULL, RequestHash TEXT NOT NULL, RawContent TEXT NOT NULL, MappingJson TEXT NOT NULL,
 NewRows INTEGER NOT NULL, DuplicateRows INTEGER NOT NULL, State TEXT NOT NULL CHECK(State IN('Active','Voided')), Version INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL,
 UNIQUE(TenantId,Platform,ShopId,Number));
CREATE TABLE BillRows(Id TEXT PRIMARY KEY, BatchId TEXT NOT NULL REFERENCES BillBatches(Id), TenantId TEXT NOT NULL, Platform TEXT NOT NULL, ShopId TEXT NOT NULL,
 TransactionId TEXT NOT NULL, ExternalOrderId TEXT NOT NULL, Kind TEXT NOT NULL CHECK(Kind IN('Sale','Refund','PlatformFee','Freight')), AmountCents INTEGER NOT NULL CHECK(AmountCents>0),
 OccurredAt TEXT NOT NULL, SourceCase TEXT NOT NULL, SourceHash TEXT NOT NULL, RawJson TEXT NOT NULL,
 OrderId TEXT REFERENCES SalesOrders(Id), CaseId TEXT REFERENCES AfterSaleCases(Id), Version INTEGER NOT NULL DEFAULT 1,
 UNIQUE(TenantId,Platform,ShopId,TransactionId));
CREATE INDEX IX_BillRows_Order ON BillRows(OrderId);
CREATE TABLE SkuCosts(SkuId TEXT PRIMARY KEY, UnitCostMicros INTEGER NOT NULL CHECK(UnitCostMicros>=0 AND UnitCostMicros<=1000000000000), Version INTEGER NOT NULL, Evidence TEXT NOT NULL);
CREATE TABLE ShipmentCosts(ShipmentLineId TEXT NOT NULL REFERENCES ShipmentLines(Id), SkuId TEXT NOT NULL, QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0),
 UnitCostMicros INTEGER, CostCents INTEGER, Version INTEGER NOT NULL DEFAULT 1, Source TEXT NOT NULL,
 PRIMARY KEY(ShipmentLineId,SkuId), CHECK((UnitCostMicros IS NULL AND CostCents IS NULL) OR (UnitCostMicros>=0 AND CostCents>=0)));
INSERT INTO ShipmentCosts(ShipmentLineId,SkuId,QuantityUnits,Source)
 SELECT c.ShipmentLineId,c.SkuId,c.QuantityUnits,'迁移历史出库，待补成本' FROM ShipmentComponents c JOIN ShipmentLines l ON l.Id=c.ShipmentLineId JOIN Shipments s ON s.Id=l.ShipmentId WHERE s.State='Shipped';
CREATE TABLE OrderFeePlans(OrderId TEXT PRIMARY KEY REFERENCES SalesOrders(Id), PlatformFeeCents INTEGER NOT NULL CHECK(PlatformFeeCents>=0), FreightCents INTEGER NOT NULL CHECK(FreightCents>=0), Version INTEGER NOT NULL, Evidence TEXT NOT NULL);
CREATE TABLE FinanceHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT, EntityId TEXT NOT NULL, Action TEXT NOT NULL, Actor TEXT NOT NULL, Reason TEXT NOT NULL, DataJson TEXT NOT NULL, CreatedAt TEXT NOT NULL);
CREATE TABLE FinanceExports(Id TEXT PRIMARY KEY, Number TEXT NOT NULL UNIQUE, Target TEXT NOT NULL, RequestHash TEXT NOT NULL, ContentHash TEXT NOT NULL,
 JsonContent TEXT NOT NULL, CsvContent TEXT NOT NULL, CsvHash TEXT NOT NULL, State TEXT NOT NULL CHECK(State IN('Prepared','Exported')), Version INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL);
CREATE TABLE FinanceExportOrders(ExportId TEXT NOT NULL REFERENCES FinanceExports(Id), OrderId TEXT NOT NULL REFERENCES SalesOrders(Id), Fingerprint TEXT NOT NULL, PRIMARY KEY(ExportId,OrderId));
CREATE TABLE FinanceExportRows(ExportId TEXT NOT NULL REFERENCES FinanceExports(Id), RowId TEXT NOT NULL REFERENCES BillRows(Id), PRIMARY KEY(ExportId,RowId));
