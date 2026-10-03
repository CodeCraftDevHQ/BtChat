using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace BtChat;

public class WindowsBluetoothTransport : IBluetoothTransport
{
    static readonly Guid ServiceGuid = Guid.Parse(Protocol.ServiceId);

    readonly Channel<StreamSocket> incoming = Channel.CreateUnbounded<StreamSocket>();
    readonly SemaphoreSlim startLock = new(1, 1);
    RfcommServiceProvider? provider;
    StreamSocketListener? listener;

    public Task<bool> EnableAsync() => Task.FromResult(false);

    public async Task<BtState> GetStateAsync()
    {
        var adapter = await Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync();
        AppLog.Write("BT-WIN", adapter == null
            ? "no bluetooth adapter"
            : $"adapter classic={adapter.IsClassicSupported} le={adapter.IsLowEnergySupported} central={adapter.IsCentralRoleSupported} peripheral={adapter.IsPeripheralRoleSupported}");
        return adapter != null ? BtState.Ready : BtState.Unavailable;
    }

    public async Task<IReadOnlyList<BtDevice>> GetPairedDevicesAsync()
    {
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        var list = infos.Select(i => new BtDevice(i.Name, i.Id)).ToList();
        AppLog.Write("BT-WIN", $"paired devices: {string.Join(", ", list.Select(d => d.Name))}");
        return list;
    }

    public async Task<Stream> ConnectAsync(BtDevice device, CancellationToken ct)
    {
        AppLog.Write("BT-WIN", $"connect start to {device.Name}");
        var bt = await BluetoothDevice.FromIdAsync(device.Id) ?? throw new InvalidOperationException("FromIdAsync returned null");
        AppLog.Write("BT-WIN", $"device opened status={bt.ConnectionStatus} pairing={bt.DeviceInformation.Pairing.IsPaired}");
        var socket = new StreamSocket();
        try
        {
            var result = await bt.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(ServiceGuid), BluetoothCacheMode.Uncached);
            AppLog.Write("BT-WIN", $"service lookup error={result.Error} count={result.Services.Count}");
            var service = result.Services.FirstOrDefault() ?? throw new InvalidOperationException("service not found on remote device");
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
        }
        catch (Exception ex)
        {
            AppLog.Error("BT-WIN", "connect failed", ex);
            socket.Dispose();
            bt.Dispose();
            throw;
        }
        AppLog.Write("BT-WIN", "connect ok");
        return new DuplexStream(socket.InputStream.AsStreamForRead(), socket.OutputStream.AsStreamForWrite(), () =>
        {
            socket.Dispose();
            bt.Dispose();
        }, "bt-win-out");
    }

    async Task EnsureAdvertisingAsync()
    {
        await startLock.WaitAsync();
        try
        {
            if (listener != null) return;
            AppLog.Write("BT-WIN", "start advertising");
            var newProvider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceGuid));
            var newListener = new StreamSocketListener();
            newListener.ConnectionReceived += (_, e) =>
            {
                AppLog.Write("BT-WIN", $"incoming connection from {e.Socket.Information.RemoteHostName?.DisplayName}");
                incoming.Writer.TryWrite(e.Socket);
            };
            await newListener.BindServiceNameAsync(newProvider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
            newProvider.StartAdvertising(newListener, true);
            provider = newProvider;
            listener = newListener;
            AppLog.Write("BT-WIN", "advertising started");
        }
        catch (Exception ex)
        {
            AppLog.Error("BT-WIN", "advertising failed", ex);
            throw;
        }
        finally
        {
            startLock.Release();
        }
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        await EnsureAdvertisingAsync();
        AppLog.Write("BT-WIN", "waiting for incoming connection");
        var socket = await incoming.Reader.ReadAsync(ct);
        AppLog.Write("BT-WIN", "accepted");
        return new DuplexStream(socket.InputStream.AsStreamForRead(), socket.OutputStream.AsStreamForWrite(), socket.Dispose, "bt-win-in");
    }
}
