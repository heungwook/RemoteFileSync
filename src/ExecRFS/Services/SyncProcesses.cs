namespace ExecRFS.Services;

public sealed class SyncProcesses
{
    public ProcessManager Server { get; }
    public ProcessManager Client { get; }

    public SyncProcesses(ProcessManager server, ProcessManager client)
    {
        Server = server;
        Client = client;
    }
}
