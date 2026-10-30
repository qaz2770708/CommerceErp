using System.Security.Cryptography;
using System.Text;
using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;
using static CommerceErp.Core.BusinessUnits;
namespace CommerceErp.Infrastructure;

public sealed partial class MasterDataService(CommerceDatabase database,Action<string>? fault=null)
{
    public static void Text(string? value,string name,int max=100)
    {if(string.IsNullOrWhiteSpace(value)||value.Length>max||value!=value.Trim()||value.Any(char.IsControl))throw new BusinessRuleException(name+"不能为空、含首尾空白或控制字符，且不能超长。");}
    private static string Digest(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(value))));
    private static string Actor()=>SecurityContext.Current is{} current?AccountService.Actor(current.User):"本机工具";
    private static void History(DbSession db,string entity,string action,string reason,object data)=>db.Execute("INSERT INTO MasterHistory VALUES($id,$entity,$action,$actor,$reason,$now,$json)",("id",Guid.NewGuid().ToString("N")),("entity",entity),("action",action),("actor",Actor()),("reason",reason),("now",Now()),("json",Json(data)));
    private MasterResult Command(string operation,string kind,object input,string reason,Func<DbSession,MasterResult> action)
    {
        if(!Guid.TryParse(operation,out var guid)||guid==Guid.Empty)throw new BusinessRuleException("须使用有效操作号，重试保留原操作号。");Text(reason,"操作原因",500);var hash=Digest(input);
        return database.Write(db=>
        {
            var old=db.Query("SELECT Kind,RequestHash,ResponseJson FROM Operations WHERE TenantId=$t AND OperationId=$op",r=>(Kind:r.GetString(0),Hash:r.GetString(1),Json:r.GetString(2)),("t",Tenant(db)),("op",operation)).SingleOrDefault();
            if(old.Json is not null){if(old.Kind!=kind||old.Hash!=hash)throw new ConflictException("操作号已用于不同请求。");return FromJson<MasterResult>(old.Json) with{Replayed=true};}
            var result=action(db);db.Execute("INSERT INTO Operations VALUES($t,$op,$kind,$hash,$json,$now)",("t",Tenant(db)),("op",operation),("kind",kind),("hash",hash),("json",Json(result)),("now",Now()));fault?.Invoke("before-commit");return result;
        });
    }
    public static WorkspaceProfile Profile(DbSession db)
    {
        var data=db.Query("SELECT Mode,Version FROM MasterWorkspace WHERE Id=1",r=>(Mode:r.GetString(0),Version:r.GetInt64(1))).Single();
        var shops=db.Query("SELECT s.Id,s.Platform,x.ExternalId,s.Name,x.Enabled,x.Version FROM Shops s JOIN MasterShopData x ON x.ShopId=s.Id WHERE s.TenantId=$t"+SecurityContext.ShopFilter("s").Replace("s.ShopId","s.Id",StringComparison.Ordinal)+" ORDER BY s.Platform,s.Id",r=>new ShopProfile(r.GetString(0),Enum.Parse<Platform>(r.GetString(1)),r.GetString(2),r.GetString(3),r.GetInt64(4)==1,r.GetInt64(5),"NotConnected"),("t",Tenant(db)));
        return new(data.Mode,Tenant(db),db.Scalar<string>("SELECT Name FROM Tenants WHERE Id=$t",("t",Tenant(db))),OwnerOf(db),WarehouseOf(db),db.Scalar<string>("SELECT Name FROM Warehouses WHERE Id=$w",("w",WarehouseOf(db))),data.Version,shops,SecurityContext.Current is null||ShopIds(db).All(SecurityContext.Current.User.Shops.Contains));
    }
    public WorkspaceProfile Workspace()=>database.Read(Profile);
    public MasterSnapshot Snapshot()=>database.Read(db=>new MasterSnapshot(Profile(db),Skus(db),ReadCatalog(db).Mappings,Unmapped(db)));
    private const string MissingFrom=" FROM PlatformOrderLines l JOIN SalesOrders o ON o.Id=l.OrderId JOIN MasterShopData x ON x.ShopId=o.ShopId WHERE o.TenantId=$t AND o.State='Exception' AND (CHARINDEX($search,l.ProductId)>0 OR CHARINDEX($search,l.VariantId)>0) AND NOT EXISTS(SELECT 1 FROM SkuMappings m LEFT JOIN MasterMappingState ms ON ms.TenantId=m.TenantId AND ms.Platform=m.Platform AND ms.ShopId=m.ShopId AND ms.ProductId=m.ProductId AND ms.VariantId=m.VariantId JOIN Skus sk ON sk.Id=m.SkuId AND sk.Enabled=1 WHERE m.TenantId=o.TenantId AND m.Platform=o.Platform AND m.ShopId=o.ShopId AND m.ProductId=l.ProductId AND m.VariantId=l.VariantId AND COALESCE(ms.Enabled,1)=1)";
    private const string MissingGroup=" GROUP BY o.Platform,o.ShopId,x.ExternalId,l.ProductId,l.VariantId";
    private static List<UnmappedSku> MissingRows(DbSession db,string search,int? page=null,int size=50)=>db.Query("SELECT o.Platform,o.ShopId,x.ExternalId,l.ProductId,l.VariantId,COUNT(DISTINCT o.Id)"+MissingFrom+MissingGroup+" ORDER BY o.Platform,o.ShopId,l.ProductId,l.VariantId"+(page is null?"":db.Page),r=>new UnmappedSku(Enum.Parse<Platform>(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt32(5)),("t",Tenant(db)),("search",search),("offset",((page??1)-1)*size),("limit",size));
    private static List<UnmappedSku> Unmapped(DbSession db)=>MissingRows(db,"");
    private static List<SkuProfile> Skus(DbSession db,string? code=null,string? id=null,int? limit=null)=>db.Query("SELECT s.Id,s.Code,s.Name,s.Enabled,s.ComponentsJson,x.Unit,x.Version FROM Skus s JOIN MasterSkuData x ON x.SkuId=s.Id WHERE s.TenantId=$t AND ($code IS NULL OR s.Code=$code) AND ($id IS NULL OR s.Id=$id) ORDER BY s.Code"+(limit is null?"":db.Limit(limit.Value)),r=>new SkuProfile(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3)==1,FromJson<List<SkuComponent>>(r.GetString(4)),r.GetString(5),r.GetInt64(6)),("t",Tenant(db)),("code",code),("id",id));
    public List<MasterHistory> History(string entity)=>database.Read(db=>{Text(entity,"资料标识",250);return db.Query("SELECT Entity,Action,Actor,Reason,CreatedAt,DataJson FROM MasterHistory WHERE Entity=$id ORDER BY CreatedAt DESC,Id DESC"+db.Limit(100),r=>new MasterHistory(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5)),("id",entity));});
    public MasterResult SaveWorkspace(SaveWorkspaceRequest r)=>Command(r.OperationId,"MasterWorkspace",r,r.Reason,db=>
    {
        Text(r.EnterpriseName,"企业名称");Text(r.WarehouseName,"仓库名称");var old=Profile(db);if(old.Version!=r.ExpectedVersion)throw new ConflictException("基础资料已变化，请刷新。");
        db.Execute("UPDATE Tenants SET Name=$n WHERE Id=$t",("n",r.EnterpriseName),("t",Tenant(db)));db.Execute("UPDATE Warehouses SET Name=$n WHERE Id=$w",("n",r.WarehouseName),("w",WarehouseOf(db)));db.Execute("UPDATE MasterWorkspace SET Version=Version+1 WHERE Id=1");History(db,"workspace","Update",r.Reason,new{Before=old,After=r});return new("workspace",old.Version+1,false,"企业与仓库名称已保存，内部标识不变。");
    });
    public MasterResult SaveShop(SaveShopRequest r)=>Command(r.OperationId,"MasterShop",r,r.Reason,db=>
    {
        Text(r.ExternalId,"平台店铺标识");Text(r.Name,"店铺名称");if(!Enum.IsDefined(r.Platform))throw new BusinessRuleException("平台无效。");
        var old=Profile(db).Shops.SingleOrDefault(x=>x.Id==r.Id);if(r.Id is not null&&old is null)throw new BusinessRuleException("店铺不存在。");if((old?.Version??0)!=r.ExpectedVersion)throw new ConflictException("店铺版本已变化。");
        if(old is not null&&(old.Platform!=r.Platform||old.ExternalId!=r.ExternalId))throw new BusinessRuleException("已登记店铺的平台和外部标识不能修改，请新建正确店铺。");
        if(db.Scalar<int>("SELECT COUNT(*) FROM Shops s JOIN MasterShopData x ON x.ShopId=s.Id WHERE s.Platform=$p AND x.ExternalId=$ext AND s.Id<>$id",("p",r.Platform.ToString()),("ext",r.ExternalId),("id",r.Id??""))>0)throw new ConflictException("该平台店铺已经登记。");
        var id=old?.Id??"shop-"+Guid.NewGuid().ToString("N");var version=(old?.Version??0)+1;
        if(old is null){db.Execute("INSERT INTO Shops VALUES($t,$p,$id,$name)",("t",Tenant(db)),("p",r.Platform.ToString()),("id",id),("name",r.Name));db.Execute("INSERT INTO MasterShopData VALUES($id,$ext,$enabled,1)",("id",id),("ext",r.ExternalId),("enabled",r.Enabled?1:0));
            db.Execute("INSERT INTO SecurityUserShops SELECT Id,$shop FROM SecurityUsers WHERE Role='Administrator'",("shop",id));}
        else{db.Execute("UPDATE Shops SET Name=$name WHERE Id=$id",("name",r.Name),("id",id));db.Execute("UPDATE MasterShopData SET Enabled=$e,Version=$v WHERE ShopId=$id",("e",r.Enabled?1:0),("v",version),("id",id));}
        History(db,id,"SaveShop",r.Reason,new{Before=old,After=r});return new(id,version,false,"店铺已保存；登记不代表平台授权成功。");
    });
    public MasterResult SaveSku(SaveSkuRequest r)=>Command(r.OperationId,"MasterSku",r,r.Reason,db=>
    {
        Text(r.Code,"SKU 编码");Text(r.Name,"商品名称");Text(r.Unit,"单位",20);if(r.Components is null||r.Components.Count>100||r.Components.Any(x=>x is null)||r.Components.Select(x=>x.SkuId).Distinct().Count()!=r.Components.Count)throw new BusinessRuleException("组件须唯一且最多 100 行。");
        var all=Skus(db);var old=all.SingleOrDefault(x=>x.Id==r.Id);if(r.Id is not null&&old is null)throw new BusinessRuleException("SKU 不存在。");if((old?.Version??0)!=r.ExpectedVersion)throw new ConflictException("SKU 已变化，请刷新。");if(all.Any(x=>x.Code==r.Code&&x.Id!=r.Id))throw new ConflictException("SKU 编码已存在。");
        if(old is not null&&old.Code!=r.Code)throw new BusinessRuleException("SKU 编码创建后不可更改。");
        foreach(var c in r.Components){var child=all.SingleOrDefault(x=>x.Id==c.SkuId);if(child is null||!child.Enabled||child.Components.Count>0||c.SkuId==r.Id||c.Quantity<=0||c.Quantity>1000||decimal.Round(c.Quantity,3)!=c.Quantity)throw new BusinessRuleException("组合商品须引用启用的普通 SKU，组件数量最多三位小数。");}
        if(old is not null)
        {
            var structural=old.Unit!=r.Unit||Json(old.Components)!=Json(r.Components);
            if(structural&&(db.Scalar<int>("SELECT COUNT(*) FROM InventoryMovements WHERE SkuId=$id",("id",old.Id))>0||db.Scalar<int>("SELECT COUNT(*) FROM QuarantineBalances WHERE SkuId=$id AND QuantityUnits<>0",("id",old.Id))>0||db.Scalar<int>("SELECT COUNT(*) FROM SkuBarcodes WHERE SkuId=$id",("id",old.Id))>0||db.Scalar<int>("SELECT COUNT(*) FROM InventoryLedger WHERE SkuId=$id",("id",old.Id))>0||db.Scalar<int>("SELECT COUNT(*) FROM SkuMappings WHERE SkuId=$id",("id",old.Id))>0||all.Any(x=>x.Components.Any(c=>c.SkuId==old.Id))))throw new ConflictException("已有库存记录、映射或组合引用，不能改变单位或组件结构；请新建 SKU。");
            if(!r.Enabled&&(db.Scalar<int>("SELECT COUNT(*) FROM Reservations WHERE SkuId=$id AND State='Active' AND QuantityUnits>ConsumedUnits",("id",old.Id))>0||all.Any(x=>x.Enabled&&x.Components.Any(c=>c.SkuId==old.Id))))throw new ConflictException("存在有效预占或启用的组合引用，暂不能停用。");
        }
        var id=old?.Id??"sku-"+Guid.NewGuid().ToString("N");var version=(old?.Version??0)+1;
        if(old is null){db.Execute("INSERT INTO Skus VALUES($t,$id,$code,$name,$e,$components)",("t",Tenant(db)),("id",id),("code",r.Code),("name",r.Name),("e",r.Enabled?1:0),("components",Json(r.Components)));db.Execute("INSERT INTO MasterSkuData VALUES($id,$unit,1)",("id",id),("unit",r.Unit));}
        else{db.Execute("UPDATE Skus SET Name=$name,Enabled=$e,ComponentsJson=$c WHERE Id=$id",("name",r.Name),("e",r.Enabled?1:0),("c",Json(r.Components)),("id",id));db.Execute("UPDATE MasterSkuData SET Unit=$u,Version=$v WHERE SkuId=$id",("u",r.Unit),("v",version),("id",id));}
        if(r.Components.Count==0&&db.Scalar<int>("SELECT COUNT(*) FROM InventoryBalances WHERE SkuId=$id",("id",id))==0){db.Execute("INSERT INTO InventoryBalances(TenantId,OwnerId,WarehouseId,SkuId) VALUES($t,$o,$w,$id)",("t",Tenant(db)),("o",OwnerOf(db)),("w",WarehouseOf(db)),("id",id));db.Execute("INSERT INTO QuarantineBalances(TenantId,OwnerId,WarehouseId,SkuId) VALUES($t,$o,$w,$id)",("t",Tenant(db)),("o",OwnerOf(db)),("w",WarehouseOf(db)),("id",id));}
        if(r.Components.Count>0){db.Execute("DELETE FROM QuarantineBalances WHERE SkuId=$id",("id",id));db.Execute("DELETE FROM InventoryBalances WHERE SkuId=$id",("id",id));}
        History(db,id,"SaveSku",r.Reason,new{Before=old,After=r});return new(id,version,false,"SKU 已保存；新增商品没有自动增加库存。条码在仓储条码页登记。");
    });
    internal static SkuMapping ApplyMapping(DbSession db,SkuMapping m,int expected,string reason,bool write=true)
    {
        if(m is null)throw new BusinessRuleException("映射不能为空。");RequireTenant(db,m.TenantId);if(!Enum.IsDefined(m.Platform))throw new BusinessRuleException("平台无效。");foreach(var value in new[]{m.ShopId,m.PlatformProductId,m.PlatformSkuId,m.InternalSkuId})Text(value,"映射标识");
        if(m.UnitMultiplier<=0||m.UnitMultiplier>1000||decimal.Round(m.UnitMultiplier,3)!=m.UnitMultiplier)throw new BusinessRuleException("单位倍率须为正，最多三位小数且不超过1000。");
        if(db.Scalar<int>("SELECT COUNT(*) FROM Shops s JOIN MasterShopData x ON x.ShopId=s.Id WHERE s.Id=$id AND s.Platform=$p AND x.Enabled=1",("id",m.ShopId),("p",m.Platform.ToString()))!=1)throw new BusinessRuleException("映射必须属于已启用的同平台店铺。");
        var catalog=ReadCatalog(db);if(!catalog.Skus.Any(s=>s.Id==m.InternalSkuId&&(!m.Enabled||s.Enabled)))throw new BusinessRuleException("映射目标 SKU 未启用。");
        var old=catalog.Mappings.SingleOrDefault(x=>WorkspaceStore.SameIdentity(x,m));if((old?.Version??0)!=expected)throw new ConflictException("映射已变化，请重新预览。");var next=m with{Version=expected+1};if(!write)return next;
        (string Name,object? Value)[] args=[("t",m.TenantId),("p",m.Platform.ToString()),("shop",m.ShopId),("product",m.PlatformProductId),("variant",m.PlatformSkuId),("sku",m.InternalSkuId),("m",Quantity(m.UnitMultiplier)),("v",next.Version)];
        db.Upsert("UPDATE SkuMappings SET SkuId=$sku,MultiplierUnits=$m,Version=$v WHERE TenantId=$t AND Platform=$p AND ShopId=$shop AND ProductId=$product AND VariantId=$variant","INSERT INTO SkuMappings VALUES($t,$p,$shop,$product,$variant,$sku,$m,$v)",args);
        db.Upsert("UPDATE MasterMappingState SET Enabled=$e WHERE TenantId=$t AND Platform=$p AND ShopId=$shop AND ProductId=$product AND VariantId=$variant","INSERT INTO MasterMappingState VALUES($t,$p,$shop,$product,$variant,$e)",("t",m.TenantId),("p",m.Platform.ToString()),("shop",m.ShopId),("product",m.PlatformProductId),("variant",m.PlatformSkuId),("e",m.Enabled?1:0));
        History(db,"mappings","SaveMapping",reason,new{Before=old,After=next});return next;
    }
    private static MasterPreview Preview(DbSession db,List<MappingUpdate> rows)
    {
        if(rows is null||rows.Count is <1 or >200||rows.Any(x=>x is null||x.Mapping is null))throw new BusinessRuleException("每批映射须为1–200条。");
        var errors=new List<string>();var seen=new HashSet<string>();for(int i=0;i<rows.Count;i++){var row=rows[i];try{var m=row.Mapping;if(!seen.Add(Json(new{m.Platform,m.ShopId,m.PlatformProductId,m.PlatformSkuId})))throw new BusinessRuleException("同批映射键重复。");ApplyMapping(db,m,row.ExpectedVersion,"预览",false);}catch(Exception e)when(e is BusinessRuleException or ConflictException){errors.Add($"第 {i+1} 行：{e.Message}");}}
        return new(errors.Count==0,Digest(rows),errors,rows.Count);
    }
    public MasterPreview PreviewMappings(MappingPreviewRequest r)=>database.Read(db=>Preview(db,r.Rows));
    public MasterResult SaveMappings(MappingBatchRequest r)=>Command(r.OperationId,"MasterMappings",r,r.Reason,db=>
    {var preview=Preview(db,r.Rows);if(!preview.Valid)throw new BusinessRuleException(string.Join("；",preview.Errors));if(preview.Hash!=r.PreviewHash)throw new ConflictException("内容已变更，请重新预览。");foreach(var row in r.Rows)ApplyMapping(db,row.Mapping,row.ExpectedVersion,r.Reason);return new(r.OperationId,1,false,$"已保存 {r.Rows.Count} 条映射，历史业务快照未修改。");});
    private static void CheckOpening(DbSession db,List<OpeningLine> lines)
    {
        if(lines is null||lines.Count is <1 or >200||lines.Any(x=>x is null)||lines.Select(x=>x.SkuId).Distinct().Count()!=lines.Count)throw new BusinessRuleException("期初明细须为1–200个不重复 SKU。");var skus=Skus(db);
        foreach(var line in lines)
        {
            var sku=skus.SingleOrDefault(x=>x.Id==line.SkuId);if(sku is null||!sku.Enabled||sku.Components.Count>0||sku.Version!=line.ExpectedSkuVersion)throw new ConflictException("期初 SKU 不可用或版本变化。");if(line.Quantity<=0||line.Quantity>1000000||decimal.Round(line.Quantity,3)!=line.Quantity)throw new BusinessRuleException("期初数量须大于0，最多三位小数且不超过100万。");
            if(db.Scalar<int>("SELECT COUNT(*) FROM InventoryLedger WHERE SkuId=$id",("id",line.SkuId))>0||db.Scalar<int>("SELECT COUNT(*) FROM OpeningSkuClaims WHERE SkuId=$id",("id",line.SkuId))>0)throw new ConflictException("该 SKU 已有库存流水，禁止再次录入期初；后续收货使用正常业务单据。");
            if(db.Scalar<int>("SELECT COUNT(*) FROM InventoryBalances WHERE SkuId=$id AND Version=$v AND OnHandUnits=0 AND ReservedUnits=0 AND FrozenUnits=0",("id",line.SkuId),("v",line.ExpectedBalanceVersion))!=1)throw new ConflictException("库存已变化，请重新核对期初。");
        }
    }
    public MasterResult CreateOpening(OpeningRequest r)=>Command(r.OperationId,"OpeningCreate",r,r.Reason,db=>
    {var policy=Policy(db);Text(r.Number,"期初单号");CheckOpening(db,r.Lines);if(db.Scalar<int>("SELECT COUNT(*) FROM OpeningDocuments WHERE Number=$n",("n",r.Number))>0)throw new ConflictException("期初单号已存在。");var id=Guid.NewGuid().ToString("N");db.Execute("INSERT INTO OpeningDocuments(Id,Number,State,Version,CreatedBy,ApprovedBy,Reason,CreatedAt,LinesJson,RequireDifferentApprover,PolicyVersion) VALUES($id,$n,'Draft',1,$actor,'',$reason,$now,$json,$required,$pv)",("id",id),("n",r.Number),("actor",Actor()),("reason",r.Reason),("now",Now()),("json",Json(r.Lines)),("required",policy.RequireDifferentApprover?1:0),("pv",policy.Version));History(db,id,"CreateOpening",r.Reason,r);return new(id,1,false,"期初单已保存，审核前不改变库存。");});
    public List<OpeningDocument> Openings()=>database.Read(db=>db.Query("SELECT Id,Number,State,Version,CreatedBy,ApprovedBy,Reason,CreatedAt,LinesJson FROM OpeningDocuments ORDER BY CreatedAt DESC,Id DESC"+db.Limit(200),r=>new OpeningDocument(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),FromJson<List<OpeningLine>>(r.GetString(8)))));
    public MasterResult ApproveOpening(string id,ApproveOpeningRequest r)=>Command(r.OperationId,"OpeningApprove",new{id,r},r.Reason,db=>
    {
        var doc=db.Query("SELECT State,Version,LinesJson FROM OpeningDocuments WHERE Id=$id",x=>(State:x.GetString(0),Version:x.GetInt64(1),Lines:x.GetString(2)),("id",id)).SingleOrDefault();if(doc.Lines is null||doc.State!="Draft"||doc.Version!=r.ExpectedVersion)throw new ConflictException("期初单已审核或版本变化。");var lines=FromJson<List<OpeningLine>>(doc.Lines);
        if(Policy(db).RequireDifferentApprover||db.Scalar<int>("SELECT RequireDifferentApprover FROM OpeningDocuments WHERE Id=$id",("id",id))==1){if(SecurityContext.Current is null)throw new BusinessRuleException("已启用双人审核，须通过个人账号操作。");var creator=db.Scalar<string>("SELECT CreatedBy FROM OpeningDocuments WHERE Id=$id",("id",id));var editors=db.Query("SELECT Actor FROM MasterHistory WHERE Entity=$id AND Action='EditOpening'",r=>r.GetString(0),("id",id));if(creator.EndsWith(" ("+SecurityContext.Current.User.Id+")",StringComparison.Ordinal)||editors.Any(e=>e.EndsWith(" ("+SecurityContext.Current.User.Id+")",StringComparison.Ordinal)))throw new BusinessRuleException("制单或修改人不能审核该期初单，请另一位管理员审核。");}
        CheckOpening(db,lines);
        foreach(var line in lines)
        {
            var units=Quantity(line.Quantity);db.Execute("INSERT INTO OpeningSkuClaims VALUES($sku,$doc)",("sku",line.SkuId),("doc",id));
            db.Execute("UPDATE InventoryBalances SET OnHandUnits=$q,Version=Version+1 WHERE SkuId=$s",("q",units),("s",line.SkuId));
            db.Execute("INSERT INTO InventoryLedger(TenantId,OwnerId,WarehouseId,SkuId,OperationId,Action,OnHandDelta,ReservedDelta,OnHandAfter,ReservedAfter,FrozenAfter,SafetyAfter,Reason,CreatedAt) SELECT TenantId,OwnerId,WarehouseId,SkuId,$op,'Opening',$q,0,OnHandUnits,ReservedUnits,FrozenUnits,SafetyUnits,$reason,$now FROM InventoryBalances WHERE SkuId=$s",("op",r.OperationId),("q",units),("reason",$"期初单 {id}：{r.Reason}"),("now",Now()),("s",line.SkuId));
            var version=db.Scalar<long>("SELECT Version FROM InventoryBalances WHERE SkuId=$s",("s",line.SkuId));db.Execute("INSERT INTO Outbox(Id,TenantId,AggregateId,AggregateVersion,EventType,PayloadJson,CreatedAt,UpdatedAt,NextAttemptAt) VALUES($id,$t,$agg,$v,'StockReceived',$json,$now,$now,$now)",("id",Guid.NewGuid().ToString("N")),("t",Tenant(db)),("agg","stock:"+line.SkuId),("v",version),("json",Json(new{skuId=line.SkuId,openingId=id,quantity=line.Quantity})),("now",Now()));fault?.Invoke("after-opening-line");
        }
        db.Execute("UPDATE OpeningDocuments SET State='Approved',Version=Version+1,ApprovedBy=$actor WHERE Id=$id",("actor",Actor()),("id",id));History(db,id,"ApproveOpening",r.Reason,new{Lines=lines,Actor=Actor()});return new(id,doc.Version+1,false,"期初单已审核，库存与流水已在同一事务保存。");
    });
}
