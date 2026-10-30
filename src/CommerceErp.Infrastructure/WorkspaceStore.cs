using CommerceErp.Core;
using static CommerceErp.Infrastructure.CommerceDatabase;

namespace CommerceErp.Infrastructure;

public sealed record WorkspaceState(int SchemaVersion, List<PlatformReadiness> Readiness,
    List<SkuMapping> Mappings, List<ValidationReport> Reports);

// Compatibility facade for the stage-1 workbench. Current state now lives in commerce.db.
public sealed class WorkspaceStore : IDisposable
{
    public CommerceDatabase Database { get; }
    private readonly string _mode = "LocalTransactionalDemo";
    public WorkspaceStore(string directory) => Database = new(directory);
    public WorkspaceStore(CommerceDatabase database, string? mode = null)
    { Database = database; _mode = mode ?? (database.IsSqlServer ? "SharedServiceDemo" : "LocalTransactionalDemo"); }
    public Task<BootstrapData> ReadAsync() => Task.FromResult(Database.Read(db =>
    {
        var user = SecurityContext.Current?.User;
        bool Shop(string id) => user is null || user.Shops.Contains(id);
        var profile=MasterDataService.Profile(db);
        var platforms=SeedData.Platforms.Where(p=>user?.Role==AccountRole.Administrator||user is null||profile.Shops.Any(s=>s.Platform==p.Id)).ToList();
        var readiness = db.Query("SELECT Platform,Revision,ItemsJson FROM Readiness", r => new PlatformReadiness(Enum.Parse<Platform>(r.GetString(0)),r.GetInt32(1),FromJson<List<CheckEntry>>(r.GetString(2)))).Where(x => platforms.Any(p => p.Id == x.Platform)).ToList();
        var context=MasterDataService.BuildContext(db);var catalog = new Catalog(context.Skus.Select(s=>new InternalSku(s.Id,s.Code,s.Name,s.Enabled,s.Components)).ToList(),context.Mappings);
        return new BootstrapData(profile.Mode=="Business"?"BusinessWorkspace":_mode,false,"2026-10-30",platforms,SeedData.Checklists(),readiness,new(catalog.Skus,catalog.Mappings.Where(m => Shop(m.ShopId)).ToList()),(profile.Mode=="Business"?[]:SeedData.Samples().Where(s => Shop(s.Order.ShopId)).Select(s => s.Descriptor).ToList()),profile);
    }));
    public Task<PlatformReadiness> SaveReadinessAsync(Platform platform, ReadinessUpdate update)
    {
        if (!Enum.IsDefined(platform) || update.Items is null) throw new BusinessRuleException("平台或检查项无效。");
        var definitions = SeedData.Checklists()[platform];
        if (update.Items.Count != definitions.Count || update.Items.Any(i => i is null) ||
            update.Items.Select(i => i.Key).Distinct().Count() != definitions.Count || update.Items.Any(i => definitions.All(d => d.Key != i.Key)))
            throw new BusinessRuleException("检查项必须完整且唯一。");
        foreach (var item in update.Items)
        {
            if (!Enum.IsDefined(item.Status) || item.Evidence is null || item.Evidence.Length > 1500) throw new BusinessRuleException("核实状态或备注不合法。");
            if (item.Status == CheckStatus.Verified && string.IsNullOrWhiteSpace(item.Evidence)) throw new BusinessRuleException("已核实项必须填写核实依据。");
        }
        return Task.FromResult(Database.Write(db =>
        {
            var current = db.Scalar<long>("SELECT Revision FROM Readiness WHERE Platform=$p", ("p", platform.ToString()));
            if (current != update.ExpectedRevision) throw new ConflictException("核实记录已更新，请重新载入后保存。");
            var next = new PlatformReadiness(platform, checked((int)current + 1), update.Items.Select(i => i with { Evidence = i.Evidence.Trim() }).ToList());
            db.Execute("UPDATE Readiness SET Revision=$v,ItemsJson=$items WHERE Platform=$p", ("v", next.Revision), ("items", Json(next.Items)), ("p", platform.ToString()));
            return next;
        }));
    }
    public Task<SkuMapping> SaveMappingAsync(MappingUpdate update)=>Task.FromResult(Database.Write(db=>MasterDataService.ApplyMapping(db,update.Mapping,update.ExpectedVersion,"单条映射维护")));
    public Task<ValidationReport> ValidateAsync(SampleOrder order) => Task.FromResult(Database.Write(db =>
    {
        var report = new OrderNormalizer().Validate(order, ReadCatalog(db),BusinessMode(db));
        db.Execute("INSERT INTO ValidationReports VALUES($id,$at,$json)", ("id", report.ReportId), ("at", report.CreatedAt.ToString("O")), ("json", Json(report)));
        db.Execute("DELETE FROM ValidationReports WHERE Id NOT IN (SELECT Id FROM ValidationReports ORDER BY CreatedAt DESC,Id DESC" + db.Limit(200) + ")");
        return report;
    }));
    public Task<List<ValidationReport>> ReportsAsync() => Task.FromResult(Database.Read(db =>
        db.Query("SELECT ReportJson FROM ValidationReports ORDER BY CreatedAt DESC,Id DESC", r => FromJson<ValidationReport>(r.GetString(0)))));
    public static bool SameIdentity(SkuMapping a, SkuMapping b) => a.TenantId == b.TenantId && a.Platform == b.Platform &&
        a.ShopId == b.ShopId && a.PlatformProductId == b.PlatformProductId && a.PlatformSkuId == b.PlatformSkuId;
    public void Dispose() { }
}
