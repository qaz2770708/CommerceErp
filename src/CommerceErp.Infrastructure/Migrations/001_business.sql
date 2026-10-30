CREATE TABLE IF NOT EXISTS SchemaMigrations(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);
CREATE TABLE Tenants(Id TEXT PRIMARY KEY, Name TEXT NOT NULL);
CREATE TABLE Shops(TenantId TEXT NOT NULL REFERENCES Tenants(Id), Platform TEXT NOT NULL, Id TEXT NOT NULL,
    Name TEXT NOT NULL, PRIMARY KEY(TenantId,Platform,Id));
CREATE TABLE Warehouses(TenantId TEXT NOT NULL REFERENCES Tenants(Id), OwnerId TEXT NOT NULL, Id TEXT NOT NULL,
    Name TEXT NOT NULL, PRIMARY KEY(TenantId,OwnerId,Id));
CREATE TABLE Skus(TenantId TEXT NOT NULL REFERENCES Tenants(Id), Id TEXT NOT NULL, Code TEXT NOT NULL,
    Name TEXT NOT NULL, Enabled INTEGER NOT NULL CHECK(Enabled IN(0,1)), ComponentsJson TEXT NOT NULL,
    PRIMARY KEY(TenantId,Id), UNIQUE(TenantId,Code));
CREATE TABLE SkuMappings(TenantId TEXT NOT NULL, Platform TEXT NOT NULL, ShopId TEXT NOT NULL,
    ProductId TEXT NOT NULL, VariantId TEXT NOT NULL, SkuId TEXT NOT NULL, MultiplierUnits INTEGER NOT NULL CHECK(MultiplierUnits>0),
    Version INTEGER NOT NULL CHECK(Version>0), PRIMARY KEY(TenantId,Platform,ShopId,ProductId,VariantId),
    FOREIGN KEY(TenantId,SkuId) REFERENCES Skus(TenantId,Id));
CREATE TABLE Readiness(Platform TEXT PRIMARY KEY, Revision INTEGER NOT NULL, ItemsJson TEXT NOT NULL);
CREATE TABLE ValidationReports(Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, ReportJson TEXT NOT NULL);
CREATE TABLE InventoryBalances(TenantId TEXT NOT NULL, OwnerId TEXT NOT NULL, WarehouseId TEXT NOT NULL, SkuId TEXT NOT NULL,
    OnHandUnits INTEGER NOT NULL DEFAULT 0 CHECK(OnHandUnits>=0 AND OnHandUnits<=1000000000000),
    ReservedUnits INTEGER NOT NULL DEFAULT 0 CHECK(ReservedUnits>=0),
    FrozenUnits INTEGER NOT NULL DEFAULT 0 CHECK(FrozenUnits>=0),
    SafetyUnits INTEGER NOT NULL DEFAULT 0 CHECK(SafetyUnits>=0 AND SafetyUnits<=1000000000000),
    Version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(TenantId,OwnerId,WarehouseId,SkuId),
    CHECK(ReservedUnits+FrozenUnits<=OnHandUnits),
    FOREIGN KEY(TenantId,OwnerId,WarehouseId) REFERENCES Warehouses(TenantId,OwnerId,Id),
    FOREIGN KEY(TenantId,SkuId) REFERENCES Skus(TenantId,Id));
CREATE TABLE SalesOrders(Id TEXT PRIMARY KEY, TenantId TEXT NOT NULL, Platform TEXT NOT NULL, ShopId TEXT NOT NULL,
    ExternalOrderId TEXT NOT NULL, OwnerId TEXT NOT NULL, WarehouseId TEXT NOT NULL,
    State TEXT NOT NULL CHECK(State IN('PendingReview','Approved','Exception','OnHold','Cancelled')),
    Version INTEGER NOT NULL CHECK(Version>0), SourceHash TEXT NOT NULL, SourceJson TEXT NOT NULL,
    NormalizedJson TEXT, IssuesJson TEXT NOT NULL, PaidCents INTEGER NOT NULL, LineCount INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
    UNIQUE(TenantId,Platform,ShopId,ExternalOrderId),
    FOREIGN KEY(TenantId,Platform,ShopId) REFERENCES Shops(TenantId,Platform,Id),
    FOREIGN KEY(TenantId,OwnerId,WarehouseId) REFERENCES Warehouses(TenantId,OwnerId,Id));
CREATE INDEX IX_Orders_TenantStateDate ON SalesOrders(TenantId,State,CreatedAt DESC,Id);
CREATE TABLE OrderLines(OrderId TEXT NOT NULL REFERENCES SalesOrders(Id), ExternalLineId TEXT NOT NULL,
    SkuId TEXT NOT NULL, QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0), MappingVersion INTEGER NOT NULL,
    SnapshotJson TEXT NOT NULL, PRIMARY KEY(OrderId,ExternalLineId));
CREATE TABLE Reservations(TenantId TEXT NOT NULL, OrderId TEXT NOT NULL REFERENCES SalesOrders(Id),
    OwnerId TEXT NOT NULL, WarehouseId TEXT NOT NULL, SkuId TEXT NOT NULL, QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0),
    State TEXT NOT NULL CHECK(State IN('Active','Released')), PRIMARY KEY(TenantId,OrderId,SkuId),
    FOREIGN KEY(TenantId,OwnerId,WarehouseId,SkuId) REFERENCES InventoryBalances(TenantId,OwnerId,WarehouseId,SkuId));
CREATE TABLE InventoryLedger(Id INTEGER PRIMARY KEY AUTOINCREMENT, TenantId TEXT NOT NULL, OwnerId TEXT NOT NULL,
    WarehouseId TEXT NOT NULL, SkuId TEXT NOT NULL, OrderId TEXT REFERENCES SalesOrders(Id), OperationId TEXT NOT NULL,
    Action TEXT NOT NULL, OnHandDelta INTEGER NOT NULL, ReservedDelta INTEGER NOT NULL,
    OnHandAfter INTEGER NOT NULL, ReservedAfter INTEGER NOT NULL, FrozenAfter INTEGER NOT NULL, SafetyAfter INTEGER NOT NULL,
    Reason TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    UNIQUE(TenantId,OperationId,Action,OwnerId,WarehouseId,SkuId),
    FOREIGN KEY(TenantId,OwnerId,WarehouseId,SkuId) REFERENCES InventoryBalances(TenantId,OwnerId,WarehouseId,SkuId));
CREATE INDEX IX_Ledger_Sku ON InventoryLedger(TenantId,SkuId,Id DESC);
CREATE TABLE OrderHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT, OrderId TEXT NOT NULL REFERENCES SalesOrders(Id),
    Action TEXT NOT NULL, FromState TEXT NOT NULL, ToState TEXT NOT NULL, Reason TEXT NOT NULL,
    RuleVersion TEXT NOT NULL, CreatedAt TEXT NOT NULL);
CREATE TABLE Operations(TenantId TEXT NOT NULL REFERENCES Tenants(Id), OperationId TEXT NOT NULL,
    Kind TEXT NOT NULL, RequestHash TEXT NOT NULL, ResponseJson TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    PRIMARY KEY(TenantId,OperationId));
CREATE TABLE OrderSnapshots(OrderId TEXT NOT NULL REFERENCES SalesOrders(Id), Version INTEGER NOT NULL,
    State TEXT NOT NULL, SourceJson TEXT NOT NULL, NormalizedJson TEXT, IssuesJson TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    PRIMARY KEY(OrderId,Version));
CREATE TABLE Outbox(Id TEXT PRIMARY KEY, TenantId TEXT NOT NULL REFERENCES Tenants(Id), AggregateId TEXT NOT NULL,
    AggregateVersion INTEGER NOT NULL, EventType TEXT NOT NULL, PayloadJson TEXT NOT NULL,
    State TEXT NOT NULL DEFAULT 'Pending', CreatedAt TEXT NOT NULL,
    UNIQUE(TenantId,AggregateId,AggregateVersion,EventType));
