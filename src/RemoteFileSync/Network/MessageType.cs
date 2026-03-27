namespace RemoteFileSync.Network;

public enum MessageType : byte
{
    Handshake = 0x01,
    HandshakeAck = 0x02,
    Manifest = 0x03,
    SyncPlan = 0x04,
    FileStart = 0x05,
    FileChunk = 0x06,
    FileEnd = 0x07,
    BackupConfirm = 0x08,
    SyncComplete = 0x09,
    DeleteFile = 0x0A,
    DeleteConfirm = 0x0B,
    Error = 0xFF
}
