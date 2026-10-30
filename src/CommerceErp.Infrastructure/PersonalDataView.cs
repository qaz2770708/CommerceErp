using CommerceErp.Core;
namespace CommerceErp.Infrastructure;
public static class PersonalDataView
{
    public static bool Allowed=>SecurityContext.Current is null || AccountPermissions.Allows(SecurityContext.Current.User,"personal.read");
    public static string Hidden(string value)=>value.Length==0?"":"[已隐藏]";
    public static ShippingAddress Address(ShippingAddress value)=>Allowed?value:new(Hidden(value.Recipient),Hidden(value.Phone),value.Province,value.City,Hidden(value.District),Hidden(value.Street),Hidden(value.PostalCode));
    public static ShipmentSummary Summary(ShipmentSummary value)=>Allowed?value:value with{Recipient=Hidden(value.Recipient)};
    public static ShipmentDetails Details(ShipmentDetails value)=>Allowed?value:value with
    {
        Summary=Summary(value.Summary),Address=Address(value.Address),
        // Free-form historical data can contain copied customer details, so do not return it to observers.
        History=value.History.Select(h=>h with{Reason=Hidden(h.Reason),Data=Hidden(h.Data)}).ToList(),
        Deliveries=value.Deliveries.Select(Delivery).ToList()
    };
    public static DeliveryJobView Delivery(DeliveryJobView value)=>Allowed?value:value with{LastError=Hidden(value.LastError),Receipt=Hidden(value.Receipt)};
}
