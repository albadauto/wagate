namespace WaGate.Domain;

/// <summary>Nomes de evento que o WaGate envia aos webhooks dos clientes.</summary>
public static class WebhookEvents
{
    public const string MessageReceived = "message.received";
    public const string MessageSent = "message.sent";
    public const string MessageStatus = "message.status";
    public const string MessageReaction = "message.reaction";
    public const string MessageRevoked = "message.revoked";
    public const string InstanceStatus = "instance.status";
    public const string InstanceConnected = "instance.connected";
    public const string InstanceDisconnected = "instance.disconnected";
    public const string Call = "call.received";
    public const string GroupJoin = "group.join";
    public const string GroupLeave = "group.leave";
    public const string Presence = "presence.update";
    public const string Test = "webhook.test";

    public static readonly string[] All =
    [
        MessageReceived, MessageSent, MessageStatus, MessageReaction, MessageRevoked,
        InstanceStatus, InstanceConnected, InstanceDisconnected,
        Call, GroupJoin, GroupLeave, Presence
    ];
}
