using System.Windows.Interop;

namespace QingJie.Core;

/// <summary>全局热键注册（基于隐藏消息窗口 + RegisterHotKey）。</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x5151;

    private HwndSource? _source;
    private Hotkey? _current;

    public event Action? Pressed;

    public Hotkey? Current => _current;

    /// <summary>注册热键；失败返回 false（通常是被其它软件占用）。</summary>
    public bool Register(Hotkey hotkey)
    {
        EnsureSource();
        Unregister();

        if (!hotkey.IsValid) return false;

        // MOD_NOREPEAT：长按不重复触发
        bool ok = Native.RegisterHotKey(_source!.Handle, HotkeyId, (uint)hotkey.Modifiers | 0x4000, hotkey.VirtualKey);
        if (ok)
        {
            _current = hotkey;
            Logger.Info($"热键已注册：{hotkey}");
        }
        else
        {
            Logger.Warn($"热键注册失败（可能被占用）：{hotkey}");
        }

        return ok;
    }

    public void Unregister()
    {
        if (_source is not null && _current is not null)
        {
            Native.UnregisterHotKey(_source.Handle, HotkeyId);
        }

        _current = null;
    }

    private void EnsureSource()
    {
        if (_source is not null) return;

        var parameters = new HwndSourceParameters("QingJie.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = -10000,
            PositionY = -10000,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.Dispose();
        _source = null;
    }
}
