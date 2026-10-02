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

    public async Task<bool> EnsurePermissionsAsync()
    {
        var status = await Permissions.CheckStatusAsync<BluetoothPermission>();
        AppLog.Write("BT-AND", $"permission status={status}");
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<BluetoothPermission>();
            AppLog.Write("BT-AND", $"permission after request={status}");
        }
        var adapter = BluetoothAdapter.DefaultAdapter;
        AppLog.Write("BT-AND", $"adapter present={adapter != null} enabled={adapter?.IsEnabled}");
        return status == PermissionStatus.Granted && adapter?.IsEnabled == true;
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
