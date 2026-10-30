namespace CommerceErp.Core;
public sealed record WorkspaceInitialization(string EnterpriseName,string WarehouseName);
public sealed record WorkspaceProfile(string Mode,string TenantId,string EnterpriseName,string OwnerId,string WarehouseId,string WarehouseName,long Version,List<ShopProfile> Shops,bool FullShopScope=true);
public sealed record ShopProfile(string Id,Platform Platform,string ExternalId,string Name,bool Enabled,long Version,string AuthorizationState);
public sealed record SkuProfile(string Id,string Code,string Name,bool Enabled,List<SkuComponent> Components,string Unit,long Version);
public sealed record MasterSnapshot(WorkspaceProfile Workspace,List<SkuProfile> Skus,List<SkuMapping> Mappings,List<UnmappedSku>? Unmapped=null);
public sealed record MasterResult(string Id,long Version,bool Replayed,string Message);
public sealed record SaveWorkspaceRequest(string OperationId,long ExpectedVersion,string EnterpriseName,string WarehouseName,string Reason);
public sealed record SaveShopRequest(string OperationId,string? Id,long ExpectedVersion,Platform Platform,string ExternalId,string Name,bool Enabled,string Reason);
public sealed record SaveSkuRequest(string OperationId,string? Id,long ExpectedVersion,string Code,string Name,string Unit,bool Enabled,List<SkuComponent> Components,string Reason);
public sealed record MappingBatchRequest(string OperationId,List<MappingUpdate> Rows,string PreviewHash,string Reason);
public sealed record MappingPreviewRequest(List<MappingUpdate> Rows);
public sealed record MasterPreview(bool Valid,string Hash,List<string> Errors,int Rows);
public sealed record OpeningLine(string SkuId,decimal Quantity,long ExpectedSkuVersion,long ExpectedBalanceVersion);
public sealed record OpeningRequest(string OperationId,string Number,List<OpeningLine> Lines,string Reason);
public sealed record OpeningDocument(string Id,string Number,string State,long Version,string CreatedBy,string ApprovedBy,string Reason,string CreatedAt,List<OpeningLine> Lines);
public sealed record ApproveOpeningRequest(string OperationId,long ExpectedVersion,string Reason);
public sealed record MasterHistory(string Entity,string Action,string Actor,string Reason,string CreatedAt,string DataJson);
public sealed record WorkerHealth(string Name,string LastSeen,string LastSuccess,string LastError,int ConsecutiveErrors);
public sealed record TaskHealth(List<WorkerHealth> Workers,int ExpiredLeases,int Exhausted,int OldPending,string Message);

public sealed record UnmappedSku(Platform Platform,string ShopId,string ExternalShopId,string ProductId,string VariantId,int Orders);

public sealed record EditOpeningRequest(string OperationId,long ExpectedVersion,List<OpeningLine> Lines,string Reason);
public sealed record OpeningPolicy(bool RequireDifferentApprover,long Version);
public sealed record SaveOpeningPolicyRequest(string OperationId,long ExpectedVersion,bool RequireDifferentApprover,string Reason);
public sealed record MasterPage<T>(List<T> Items,int Total,int Page,int PageSize);

public sealed record OpeningPolicyChange(string Id,long ExpectedVersion,bool RequireDifferentApprover,string RequestedBy,string State,long Version,string CreatedAt);

public sealed record MasterLookupRequest(List<string> Codes,List<string> Ids,List<SkuMapping> Keys);
public sealed record MasterLookup(List<SkuProfile> Skus,List<SkuMapping> Mappings);
public sealed record ProductChoice(string Label,SkuMapping Mapping);
