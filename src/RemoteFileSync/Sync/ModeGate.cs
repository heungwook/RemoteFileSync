using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Which directions a sync mode permits. Both peers derive their loop predicates from here
/// rather than each writing its own: the client's send loop and the server's receive loop are
/// two halves of one framed conversation, and if they disagree by even one entry the stream
/// desynchronises or one side blocks until the session timeout.
/// </summary>
public static class ModeGate
{
    /// <summary>Client to server: file uploads and DeleteOnServer. Pull is server-authoritative.</summary>
    // A whitelist, not `mode != SyncMode.Pull`: the old negation returned true for (SyncMode)0,
    // which is not a defined member and reaches here from an unauthenticated peer's handshake
    // byte. This is a shared safety predicate and must fail CLOSED on anything it does not
    // recognise, not admit an unknown mode to the direction that writes to the server.
    public static bool ClientToServer(SyncMode mode) => mode switch
    {
        SyncMode.Push => true,
        SyncMode.TwoWay => true,
        _ => false,
    };

    /// <summary>Server to client: file downloads and DeleteOnClient. Push is client-authoritative.</summary>
    public static bool ServerToClient(SyncMode mode) => mode switch
    {
        SyncMode.Pull => true,
        SyncMode.TwoWay => true,
        _ => false,
    };
}
