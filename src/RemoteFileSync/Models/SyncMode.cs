namespace RemoteFileSync.Models;

/// <summary>
/// Which side is authoritative for a sync. The numeric values travel in the low 2 bits of the
/// protocol handshake's syncMode byte, so they are wire format — do not renumber them, and do
/// not add a zero member: 0 is what an unauthenticated peer sends when it sends nothing.
/// </summary>
public enum SyncMode : byte
{
    /// <summary>Client -> server. The server is made to match the client; the client is never written to.</summary>
    Push = 1,

    /// <summary>Server -> client. The client is made to match the server; the server is never written to.</summary>
    Pull = 2,

    /// <summary>Both directions, with ancestor-based conflict resolution.</summary>
    TwoWay = 3,
}
