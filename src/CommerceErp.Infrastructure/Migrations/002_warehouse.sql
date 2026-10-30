ALTER TABLE Reservations ADD COLUMN ConsumedUnits INTEGER NOT NULL DEFAULT 0 CHECK(ConsumedUnits>=0 AND ConsumedUnits<=QuantityUnits);
CREATE TABLE ShippingProfiles(OrderId TEXT PRIMARY KEY REFERENCES SalesOrders(Id), AddressJson TEXT NOT NULL, AddressHash TEXT NOT NULL,
    AllowMerge INTEGER NOT NULL CHECK(AllowMerge IN(0,1)));
CREATE TABLE Shipments(Id TEXT PRIMARY KEY, Number TEXT NOT NULL UNIQUE, TenantId TEXT NOT NULL, OwnerId TEXT NOT NULL, WarehouseId TEXT NOT NULL,
    Platform TEXT NOT NULL, ShopId TEXT NOT NULL, AddressJson TEXT NOT NULL, AddressHash TEXT NOT NULL,
    State TEXT NOT NULL CHECK(State IN('Draft','Picking','Packed','Shipped','Voided','Superseded')), Version INTEGER NOT NULL DEFAULT 1,
    Carrier TEXT NOT NULL DEFAULT '', TrackingNumber TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
    FOREIGN KEY(TenantId,OwnerId,WarehouseId) REFERENCES Warehouses(TenantId,OwnerId,Id));
CREATE INDEX IX_Shipments_StateDate ON Shipments(TenantId,State,CreatedAt DESC);
CREATE UNIQUE INDEX UX_Shipment_Tracking ON Shipments(TenantId,Carrier,TrackingNumber) WHERE TrackingNumber<>'' AND State NOT IN('Voided','Superseded');
CREATE TABLE ShipmentLines(Id TEXT PRIMARY KEY, ShipmentId TEXT NOT NULL REFERENCES Shipments(Id), OrderId TEXT NOT NULL REFERENCES SalesOrders(Id),
    ExternalLineId TEXT NOT NULL, QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0), SnapshotJson TEXT NOT NULL,
    UNIQUE(ShipmentId,OrderId,ExternalLineId));
CREATE INDEX IX_ShipmentLines_OrderLine ON ShipmentLines(OrderId,ExternalLineId);
CREATE TABLE ShipmentComponents(ShipmentLineId TEXT NOT NULL REFERENCES ShipmentLines(Id), SkuId TEXT NOT NULL, QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0),
    PRIMARY KEY(ShipmentLineId,SkuId));
CREATE TABLE ShipmentChecks(ShipmentId TEXT NOT NULL REFERENCES Shipments(Id), SkuId TEXT NOT NULL, RequiredUnits INTEGER NOT NULL CHECK(RequiredUnits>0),
    VerifiedUnits INTEGER NOT NULL DEFAULT 0 CHECK(VerifiedUnits>=0 AND VerifiedUnits<=RequiredUnits), PRIMARY KEY(ShipmentId,SkuId));
CREATE TABLE ShipmentHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT, ShipmentId TEXT NOT NULL REFERENCES Shipments(Id), Action TEXT NOT NULL,
    Actor TEXT NOT NULL, Reason TEXT NOT NULL, DataJson TEXT NOT NULL, CreatedAt TEXT NOT NULL);
CREATE TABLE ShipmentRelations(SourceId TEXT NOT NULL REFERENCES Shipments(Id), TargetId TEXT NOT NULL REFERENCES Shipments(Id), Kind TEXT NOT NULL,
    PRIMARY KEY(SourceId,TargetId));
CREATE TABLE ShipmentMovements(ShipmentId TEXT NOT NULL REFERENCES Shipments(Id), OrderId TEXT NOT NULL REFERENCES SalesOrders(Id),
    SkuId TEXT NOT NULL, QuantityUnits INTEGER NOT NULL CHECK(QuantityUnits>0), PRIMARY KEY(ShipmentId,OrderId,SkuId));
ALTER TABLE InventoryLedger ADD COLUMN ShipmentId TEXT REFERENCES Shipments(Id);
CREATE TABLE SkuBarcodes(TenantId TEXT NOT NULL, Barcode TEXT NOT NULL, SkuId TEXT NOT NULL, Version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(TenantId,Barcode), FOREIGN KEY(TenantId,SkuId) REFERENCES Skus(TenantId,Id));
INSERT INTO SkuBarcodes SELECT TenantId,Code,Id,1 FROM Skus WHERE Enabled=1 AND ComponentsJson='[]';
CREATE TABLE WarehouseConfigurationHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT, Entity TEXT NOT NULL, EntityId TEXT NOT NULL, Actor TEXT NOT NULL,
    Reason TEXT NOT NULL, DataJson TEXT NOT NULL, CreatedAt TEXT NOT NULL);
CREATE TABLE DeliveryJobs(Id TEXT PRIMARY KEY, ShipmentId TEXT NOT NULL REFERENCES Shipments(Id), OrderId TEXT NOT NULL REFERENCES SalesOrders(Id),
    PayloadJson TEXT NOT NULL, State TEXT NOT NULL CHECK(State IN('Blocked','Pending','Processing','Unknown','Rejected','Confirmed')),
    Version INTEGER NOT NULL DEFAULT 1, Attempts INTEGER NOT NULL DEFAULT 0, NextAttemptAt TEXT, LeaseToken TEXT, LeaseUntil TEXT,
    LastError TEXT NOT NULL DEFAULT '', Receipt TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
    UNIQUE(ShipmentId,OrderId));
CREATE INDEX IX_DeliveryJobs_Queue ON DeliveryJobs(State,NextAttemptAt);
CREATE TABLE DeliveryAttempts(Id INTEGER PRIMARY KEY AUTOINCREMENT, JobId TEXT NOT NULL REFERENCES DeliveryJobs(Id), LeaseToken TEXT NOT NULL,
    Outcome TEXT NOT NULL, Message TEXT NOT NULL, Receipt TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL);
