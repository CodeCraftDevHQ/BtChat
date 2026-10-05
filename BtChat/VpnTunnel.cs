namespace BtChat;

public enum VpnStartResult
{
    Started,
    PermissionDenied,
    Failed
}

public interface IVpnTunnel
{
    bool IsSupported { get; }
    bool IsRunning { get; }
    long BytesDown { get; }
    long BytesUp { get; }
    int ActiveFlows { get; }
    string? LastError { get; }
    event Action? StateChanged;
    Task<VpnStartResult> StartAsync(string host, int port, string? user, string? password);
    void Stop();
}

#if !ANDROID
public sealed class NoVpnTunnel : IVpnTunnel
{
    public bool IsSupported => false;
    public bool IsRunning => false;
    public long BytesDown => 0;
    public long BytesUp => 0;
    public int ActiveFlows => 0;
    public string? LastError => null;
    public event Action? StateChanged { add { } remove { } }
    public Task<VpnStartResult> StartAsync(string host, int port, string? user, string? password) => Task.FromResult(VpnStartResult.Failed);
    public void Stop() { }
}
#endif
