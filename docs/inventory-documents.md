# 库存单据设计与验收（6.6）

InventoryDocuments 保存类型、Draft/Approved/Voided/Reversed状态、版本、创建/审核账号、原因、依据、原冲销关联及账面/变动快照。InventoryDocumentHistory 保存创建、修改、作废、审核和冲销历史。InventoryMovements 保存每SKU良品/隔离增减、入账后余额与版本。InventoryReversals 的 OriginalId 主键保证单次成功冲销，冲销单不能再冲销。

每批1–200个不重复普通SKU。请求捕获SKU版本、良品版本和隔离版本，创建/修改/审核均核对；跨行更新和操作回执同事务保存。负向变动不得侵占预占/冻结。盘点按实盘数量减捕获账面生成差异，零差异也可留审核记录。

对良品变动写InventoryLedger.StockDocumentId和StockAdjusted事件；隔离变化写InventoryMovements，原退货收货流水保持原CaseId/ReceiptId契约，售后核对加计库存单据隔离增减。

## 接口

- GET /api/inventory-documents：page/size/search/state分页，每页最多200。
- GET /api/inventory-documents/{id}：原单、数量流水和历史。
- GET /api/inventory-documents/balance/{sku}、by-code/{code}：账面和三类版本。
- POST /api/inventory-documents：建草稿。
- POST /api/inventory-documents/{id}/edit、void、approve、reverse：版本化命令。
- GET /api/inventory-documents/audit：单据明细、冲销关系与主库流水核对。

stockdocs.read/write/approve分别控制读取、制单、审核，接口显式要求全店铺及仓库范围。服务端真实账号绑定操作号；不同账号不能接管已处理请求。制单和所有修改账号不能审核本单。

## 已执行与未执行

SQLite专项覆盖草稿不改账、正负调整、盘点、隔离处置、故障回滚、并发审核、冲销竞争、历史退货隔离及权限/幂等。WPF覆盖独立账号审核、盘点/隔离/冲销与最小尺寸。SQL升级脚本已解析，并在实机入口加入库存专项；无SQL实例，本次未执行该专项。

仅数量管理，不做存货估值或费用凭证；退供不处理供应商应付。盘点不冻结全仓，也不自动撤销订单预占。数量调整不能代替真实采购、发货或售后业务来源。
