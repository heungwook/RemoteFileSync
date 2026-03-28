namespace ExecRFS.Services;

public sealed class ProcessManager : IDisposable
{
    private readonly string _role;
    public ProcessManager(string role) { _role = role; }
    public void Dispose() { }
}
