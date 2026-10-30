CREATE TABLE OpeningConfiguration(Id INTEGER PRIMARY KEY CHECK(Id=1),RequireDifferentApprover INTEGER NOT NULL CHECK(RequireDifferentApprover IN(0,1)),Version INTEGER NOT NULL);
CREATE TABLE OpeningPolicyChanges(Id TEXT PRIMARY KEY,ExpectedVersion INTEGER NOT NULL,RequireDifferentApprover INTEGER NOT NULL,RequestedBy TEXT NOT NULL,State TEXT NOT NULL,Version INTEGER NOT NULL,CreatedAt TEXT NOT NULL);
ALTER TABLE OpeningDocuments ADD COLUMN RequireDifferentApprover INTEGER NOT NULL DEFAULT 0;
ALTER TABLE OpeningDocuments ADD COLUMN PolicyVersion INTEGER NOT NULL DEFAULT 0;
CREATE TABLE PlatformOrderLines(OrderId TEXT NOT NULL REFERENCES SalesOrders(Id),LineId TEXT NOT NULL,ProductId TEXT NOT NULL,VariantId TEXT NOT NULL,PRIMARY KEY(OrderId,LineId));
CREATE INDEX IX_PlatformOrderLines_Key ON PlatformOrderLines(ProductId,VariantId,OrderId);
