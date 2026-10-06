using Android.Content;
using Android.Net;

namespace BtChat;

public sealed class AndroidVpnTunnel : IVpnTunnel
{
    const string Tag = "TUN";

    public AndroidVpnTunnel()
    {
        BtVpnService.Changed += () => StateChanged?.Invoke();
    }

    public bool IsSupported => true;
    public bool IsRunning => BtVpnService.IsRunning;
    public long BytesDown => BtVpnService.BytesDown;
    public long BytesUp => BtVpnService.BytesUp;
    public int ActiveFlows => BtVpnService.ActiveFlows;
    public string? LastError => BtVpnService.LastError;
    public event Action? StateChanged;

    public async Task<VpnStartResult> StartAsync(string host, int port, string? user, string? password)
    {
        var context = Android.App.Application.Context;
        try
        {
            var prepare = VpnService.Prepare(context);
            if (prepare != null)
            {
                var activity = Platform.CurrentActivity;
                if (activity == null) return VpnStartResult.Failed;
                var permission = new TaskCompletionSource<bool>();
                MainActivity.VpnPermissionResult = permission;
                try
                {
                    activity.StartActivityForResult(prepare, MainActivity.VpnRequest);
                    if (!await permission.Task) return VpnStartResult.PermissionDenied;
                }
                finally
                {
                    MainActivity.VpnPermissionResult = null;
                }
            }

            var completed = new TaskCompletionSource<string?>();
            BtVpnService.StartCompleted = completed;
            BtVpnService.Pending = new VpnConfig(host, port, user, password);
            context.StartService(new Intent(context, typeof(BtVpnService)));

            var finished = await Task.WhenAny(completed.Task, Task.Delay(10000));
            if (finished != completed.Task) return VpnStartResult.Failed;
            var error = completed.Task.Result;
            if (error != null)
            {
                AppLog.Write(Tag, "start refused: " + error);
                return VpnStartResult.Failed;
            }
            return VpnStartResult.Started;
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "start failed", ex);
            return VpnStartResult.Failed;
        }
        finally
        {
            BtVpnService.StartCompleted = null;
            StateChanged?.Invoke();
        }
    }

    public void Stop()
    {
        try
        {
            var context = Android.App.Application.Context;
            var intent = new Intent(context, typeof(BtVpnService)).SetAction(BtVpnService.StopAction);
            context.StartService(intent);
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "stop failed", ex);
        }
    }
}
