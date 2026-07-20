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
    public static bool ClientToServer(SyncMode mode) => mode != SyncMode.Pull;

    /// <summary>Server to client: file downloads and DeleteOnClient. Push is client-authoritative.</summary>
    public static bool ServerToClient(SyncMode mode) => mode != SyncMode.Push;
}
