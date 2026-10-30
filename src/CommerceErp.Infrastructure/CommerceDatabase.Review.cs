using CommerceErp.Core;
namespace CommerceErp.Infrastructure;
public sealed partial class CommerceDatabase
{
    internal static void InitializeReview(DbSession db){var old=db.Query("SELECT DataJson FROM MasterHistory WHERE Entity='opening-policy'",r=>FromJson<OpeningPolicy>(r.GetString(0))).MaxBy(x=>x.Version)??new(false,0);db.Execute("INSERT INTO OpeningConfiguration VALUES(1,$r,$v)",("r",old.RequireDifferentApprover?1:0),("v",old.Version));db.Execute("UPDATE OpeningDocuments SET RequireDifferentApprover=$r,PolicyVersion=$v",("r",old.RequireDifferentApprover?1:0),("v",old.Version));for(int offset=0;;offset+=200){var rows=db.Query("SELECT Id,SourceJson FROM SalesOrders ORDER BY Id"+db.Page,r=>(Id:r.GetString(0),Source:FromJson<SampleOrder>(r.GetString(1))),("offset",offset),("limit",200));foreach(var row in rows)IndexOrder(db,row.Id,row.Source);if(rows.Count<200)break;}}
    internal static void IndexOrder(DbSession db,string id,SampleOrder source){db.Execute("DELETE FROM PlatformOrderLines WHERE OrderId=$id",("id",id));foreach(var line in source.Lines)db.Execute("INSERT INTO PlatformOrderLines VALUES($id,$line,$p,$v)",("id",id),("line",line.ExternalLineId),("p",line.PlatformProductId),("v",line.PlatformSkuId));}
}
