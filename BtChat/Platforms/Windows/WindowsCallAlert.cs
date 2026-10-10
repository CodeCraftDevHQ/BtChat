namespace BtChat;

// A simple ring tone for incoming calls on Windows: two short beeps, a pause, again.
public sealed class WindowsCallAlert : ICallAlert
{
    readonly object gate = new();
    CancellationTokenSource? ringing;

    public void StartRinging(string name)
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            if (ringing != null) return;
            ringing = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    Console.Beep(880, 300);
                    await Task.Delay(120, cts.Token);
                    Console.Beep(880, 300);
                    await Task.Delay(1800, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "ring tone failed", ex);
            }
        });
    }

    public void StopRinging()
    {
        lock (gate)
        {
            ringing?.Cancel();
            ringing = null;
        }
    }
}
