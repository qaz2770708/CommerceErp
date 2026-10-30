CREATE TABLE AfterSaleCases(Id TEXT PRIMARY KEY, Number TEXT NOT NULL UNIQUE, TenantId TEXT NOT NULL, OrderId TEXT NOT NULL REFERENCES SalesOrders(Id),
    Platform TEXT NOT NULL, ShopId TEXT NOT NULL, SourceCaseNumber TEXT NOT NULL, RequestHash TEXT NOT NULL,
    Kind TEXT NOT NULL CHECK(Kind IN('CancelUnshipped','RefundOnly','ReturnRefund')),
    State TEXT NOT NULL CHECK(State IN('Open','Approved','Closed','Rejected','Withdrawn')), Version INTEGER NOT NULL DEFAULT 1,
    RefundCents INTEGER NOT NULL CHECK(RefundCents>0), RefundState TEXT NOT NULL DEFAULT 'Pending' CHECK(RefundState IN('Pending','Unknown','Failed','EvidenceRecorded')),
    RefundReference TEXT NOT NULL DEFAULT '', ShortReturnAccepted INTEGER NOT NULL DEFAULT 0 CHECK(ShortReturnAccepted IN(0,1)),
    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, UNIQUE(TenantId,Platform,ShopId,SourceCaseNumber));
CREATE INDEX IX_AfterSale_OrderState ON AfterSaleCases(OrderId,State);
CREATE INDEX IX_AfterSale_StateDate ON AfterSaleCases(TenantId,State,CreatedAt DESC);
CREATE TABLE AfterSaleLines(Id TEXT PRIMARY KEY, CaseId TEXT NOT NULL REFERENCES AfterSaleCases(Id), ShipmentLineId TEXT REFERENCES ShipmentLines(Id),
    ExternalLineId TEXT NOT NULL, QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0), SnapshotJson TEXT NOT NULL, UNIQUE(CaseId,ShipmentLineId));
CREATE TABLE ReturnComponents(CaseLineId TEXT NOT NULL REFERENCES AfterSaleLines(Id), SkuId TEXT NOT NULL, ExpectedUnits INTEGER NOT NULL CHECK(ExpectedUnits>0),
    GoodUnits INTEGER NOT NULL DEFAULT 0 CHECK(GoodUnits>=0), DamagedUnits INTEGER NOT NULL DEFAULT 0 CHECK(DamagedUnits>=0),
    PRIMARY KEY(CaseLineId,SkuId), CHECK(GoodUnits+DamagedUnits<=ExpectedUnits));
CREATE TABLE ReturnReceipts(Id TEXT PRIMARY KEY, TenantId TEXT NOT NULL, CaseId TEXT NOT NULL REFERENCES AfterSaleCases(Id),
    ReceiptNumber TEXT NOT NULL, ReturnTracking TEXT NOT NULL, RequestHash TEXT NOT NULL, Actor TEXT NOT NULL, Reason TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    UNIQUE(TenantId,ReceiptNumber));
CREATE TABLE ReturnReceiptLines(ReceiptId TEXT NOT NULL REFERENCES ReturnReceipts(Id), CaseLineId TEXT NOT NULL, SkuId TEXT NOT NULL,
    GoodUnits INTEGER NOT NULL CHECK(GoodUnits>=0), DamagedUnits INTEGER NOT NULL CHECK(DamagedUnits>=0), CHECK(GoodUnits+DamagedUnits>0),
    PRIMARY KEY(ReceiptId,CaseLineId,SkuId), FOREIGN KEY(CaseLineId,SkuId) REFERENCES ReturnComponents(CaseLineId,SkuId));
ALTER TABLE InventoryLedger ADD COLUMN AfterSaleId TEXT REFERENCES AfterSaleCases(Id);
ALTER TABLE InventoryLedger ADD COLUMN ReturnReceiptId TEXT REFERENCES ReturnReceipts(Id);
CREATE TABLE QuarantineBalances(TenantId TEXT NOT NULL, OwnerId TEXT NOT NULL, WarehouseId TEXT NOT NULL, SkuId TEXT NOT NULL,
    QuantityUnits INTEGER NOT NULL DEFAULT 0 CHECK(QuantityUnits>=0 AND QuantityUnits<=1000000000000), Version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(TenantId,OwnerId,WarehouseId,SkuId), FOREIGN KEY(TenantId,SkuId) REFERENCES Skus(TenantId,Id));
INSERT INTO QuarantineBalances(TenantId,OwnerId,WarehouseId,SkuId) SELECT TenantId,OwnerId,WarehouseId,SkuId FROM InventoryBalances;
CREATE TABLE QuarantineLedger(Id INTEGER PRIMARY KEY AUTOINCREMENT, TenantId TEXT NOT NULL, OwnerId TEXT NOT NULL, WarehouseId TEXT NOT NULL, SkuId TEXT NOT NULL,
    CaseId TEXT NOT NULL REFERENCES AfterSaleCases(Id), ReceiptId TEXT NOT NULL REFERENCES ReturnReceipts(Id), OperationId TEXT NOT NULL,
    QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0), BalanceAfter INTEGER NOT NULL, Actor TEXT NOT NULL, Reason TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    UNIQUE(TenantId,ReceiptId,SkuId));
CREATE TABLE RefundObservations(Id INTEGER PRIMARY KEY AUTOINCREMENT, TenantId TEXT NOT NULL, CaseId TEXT NOT NULL REFERENCES AfterSaleCases(Id), EventKey TEXT NOT NULL,
    RequestHash TEXT NOT NULL, Status TEXT NOT NULL CHECK(Status IN('Unknown','Failed','EvidenceRecorded')), AmountCents INTEGER NOT NULL CHECK(AmountCents>0),
    RefundReference TEXT NOT NULL, Evidence TEXT NOT NULL, ObservedAt TEXT NOT NULL, Actor TEXT NOT NULL, Reason TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    UNIQUE(TenantId,EventKey));
CREATE TABLE RefundEvidence(CaseId TEXT PRIMARY KEY REFERENCES AfterSaleCases(Id), TenantId TEXT NOT NULL, Platform TEXT NOT NULL, ShopId TEXT NOT NULL,
    RefundReference TEXT NOT NULL, AmountCents INTEGER NOT NULL CHECK(AmountCents>0), ObservationId INTEGER NOT NULL REFERENCES RefundObservations(Id),
    UNIQUE(TenantId,Platform,ShopId,RefundReference));
CREATE TABLE AfterSaleHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT, CaseId TEXT NOT NULL REFERENCES AfterSaleCases(Id), Action TEXT NOT NULL,
    Actor TEXT NOT NULL, Reason TEXT NOT NULL, DataJson TEXT NOT NULL, CreatedAt TEXT NOT NULL);
