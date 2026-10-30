namespace CommerceErp.Core;
public sealed record ServiceHealth(string Status, string Mode, string DatabaseProvider, string InstanceId,
    int SchemaVersion, int ProtocolVersion, bool LiveConnectionsEnabled, bool AuthenticationIsSharedToken, bool AccountsRequired = true);
