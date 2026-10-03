using Android.Bluetooth;
using Java.Util;

namespace BtChat;

public class BluetoothPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? new[] { (Android.Manifest.Permission.BluetoothConnect, true) }
            : Array.Empty<(string, bool)>();
}

public class AndroidBluetoothTransport : IBluetoothTransport
{
    static readonly UUID ServiceUuid = UUID.FromString(Protocol.ServiceId)!;

    BluetoothServerSocket? server;

    public async Task<BtState> GetStateAsync()
    {
        var adapter = BluetoothAdapter.DefaultAdapter;
        if (adapter == null) return BtState.Unavailable;
        var status = await Permissions.CheckStatusAsync<BluetoothPermission>();
        var state = status != PermissionStatus.Granted ? BtState.NoPermission : adapter.IsEnabled ? BtState.Ready : BtState.Off;
        AppLog.Write("BT-AND", $"state={state}");
        return state;
    }

    public async Task<bool> EnableAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity == null) return false;
        var result = new TaskCompletionSource<bool>();
        MainActivity.EnableBluetoothResult = result;
        try
        {
            activity.StartActivityForResult(new Android.Content.Intent(BluetoothAdapter.ActionRequestEnable), MainActivity.EnableBluetoothRequest);
            return await result.Task;
        }
        catch (Exception ex)
        {
            AppLog.Error("BT-AND", "enable request failed", ex);
            return false;
        }
        finally
        {
            MainActivity.EnableBluetoothResult = null;
        }
    }

    public Task<IReadOnlyList<BtDevice>> GetPairedDevicesAsync()
    {
        var list = new List<BtDevice>();
        var bonded = BluetoothAdapter.DefaultAdapter?.BondedDevices;
        if (bonded != null)
            foreach (var d in bonded)
                list.Add(new BtDevice(d.Name ?? d.Address ?? "?", d.Address ?? ""));
        AppLog.Write("BT-AND", $"paired devices: {string.Join(", ", list.Select(d => d.Name + "/" + d.Id))}");
        return Task.FromResult<IReadOnlyList<BtDevice>>(list);
    }

    public async Task<Stream> ConnectAsync(BtDevice device, CancellationToken ct)
    {
        AppLog.Write("BT-AND", $"connect start to {device.Name}/{device.Id}");
        var adapter = BluetoothAdapter.DefaultAdapter!;
        var remote = adapter.GetRemoteDevice(device.Id)!;
        var socket = remote.CreateRfcommSocketToServiceRecord(ServiceUuid)!;
        try
        {
            await Task.Run(() => socket.Connect(), ct);
        }
        catch (Exception ex)
        {
            AppLog.Error("BT-AND", "connect failed", ex);
            socket.Close();
            throw;
        }
        AppLog.Write("BT-AND", $"connect ok isConnected={socket.IsConnected}");
        return new DuplexStream(socket.InputStream!, socket.OutputStream!, () => socket.Close(), "bt-android-out");
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        if (server == null)
        {
            AppLog.Write("BT-AND", "creating server socket");
            server = BluetoothAdapter.DefaultAdapter!.ListenUsingRfcommWithServiceRecord("BtChat", ServiceUuid)!;
        }
        var current = server;
        try
        {
            AppLog.Write("BT-AND", "waiting for incoming connection");
            var socket = await Task.Run(() => current.Accept());
            AppLog.Write("BT-AND", $"accepted from {socket?.RemoteDevice?.Name}/{socket?.RemoteDevice?.Address}");
            return new DuplexStream(socket!.InputStream!, socket.OutputStream!, () => socket.Close(), "bt-android-in");
        }
        catch (Exception ex)
        {
            AppLog.Error("BT-AND", "accept failed, server socket reset", ex);
            try { current.Close(); } catch { }
            server = null;
            throw;
        }
    }
}
