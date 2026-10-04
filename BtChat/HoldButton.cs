namespace BtChat;

public class HoldButton : Button
{
    public event Action? HoldDown;
    public event Action<double>? HoldMoved;
    public event Action<bool>? HoldUp;

#if ANDROID
    Android.Views.View? attached;
    float startX;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (attached != null)
        {
            attached.Touch -= OnTouch;
            attached = null;
        }
        if (Handler?.PlatformView is Android.Views.View view)
        {
            attached = view;
            view.Touch += OnTouch;
        }
    }

    void OnTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        var motion = e.Event;
        if (motion == null || sender is not Android.Views.View view) return;
        var density = view.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
        switch (motion.ActionMasked)
        {
            case Android.Views.MotionEventActions.Down:
                startX = motion.RawX;
                view.Parent?.RequestDisallowInterceptTouchEvent(true);
                HoldDown?.Invoke();
                break;
            case Android.Views.MotionEventActions.Move:
                HoldMoved?.Invoke((motion.RawX - startX) / density);
                break;
            case Android.Views.MotionEventActions.Up:
                HoldUp?.Invoke(false);
                break;
            case Android.Views.MotionEventActions.Cancel:
                HoldUp?.Invoke(true);
                break;
        }
        e.Handled = true;
    }
#endif
}
