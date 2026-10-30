CREATE TABLE InventoryDocuments(Id TEXT PRIMARY KEY,Number TEXT NOT NULL UNIQUE,Kind TEXT NOT NULL,State TEXT NOT NULL,Version INTEGER NOT NULL,CreatedBy TEXT NOT NULL,ApprovedBy TEXT NOT NULL,Reason TEXT NOT NULL,Evidence TEXT NOT NULL,CreatedAt TEXT NOT NULL,ReversalOf TEXT NULL REFERENCES InventoryDocuments(Id),LinesJson TEXT NOT NULL);
CREATE INDEX IX_InventoryDocuments_State ON InventoryDocuments(State,CreatedAt,Id);
CREATE TABLE InventoryDocumentHistory(Id TEXT PRIMARY KEY,DocumentId TEXT NOT NULL REFERENCES InventoryDocuments(Id),Action TEXT NOT NULL,ActorId TEXT NOT NULL,Reason TEXT NOT NULL,CreatedAt TEXT NOT NULL,DataJson TEXT NOT NULL);
CREATE INDEX IX_InventoryDocumentHistory_Doc ON InventoryDocumentHistory(DocumentId,CreatedAt,Id);
CREATE TABLE InventoryMovements(DocumentId TEXT NOT NULL REFERENCES InventoryDocuments(Id),SkuId TEXT NOT NULL,OnHandDelta INTEGER NOT NULL,QuarantineDelta INTEGER NOT NULL,OnHandAfter INTEGER NOT NULL,QuarantineAfter INTEGER NOT NULL,InventoryVersion INTEGER NOT NULL,QuarantineVersion INTEGER NOT NULL,PRIMARY KEY(DocumentId,SkuId));
CREATE TABLE InventoryReversals(OriginalId TEXT PRIMARY KEY REFERENCES InventoryDocuments(Id),ReversalId TEXT NOT NULL UNIQUE REFERENCES InventoryDocuments(Id));
ALTER TABLE InventoryLedger ADD COLUMN StockDocumentId TEXT NULL REFERENCES InventoryDocuments(Id);
