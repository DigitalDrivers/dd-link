using AssettoServer.Network.ClientMessages;

namespace DDLinkPlugin;

/// <summary>
/// Sends a car back to its pit box. The script AssettoServer ships to every game (assettoserver.lua)
/// listens for this event and teleports the local car when it is named as the sender, so the packet goes
/// to one driver with that driver's own session id.
/// </summary>
[OnlineEvent(Key = "AS_TeleportToPits")]
public class TeleportToPitsPacket : OnlineEvent<TeleportToPitsPacket>
{
    [OnlineEventField(Name = "dummy")]
    public byte Dummy;
}
