using System.Runtime.InteropServices;

namespace QingJie.Core;

/// <summary>开发自检用的合成输入（仅用于 --dragtest）。</summary>
internal static class InputSim
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static bool MoveTo(int x, int y) => SetCursorPos(x, y);

    public static void LeftDown() => SendMouse(MOUSEEVENTF_LEFTDOWN);

    public static void LeftUp() => SendMouse(MOUSEEVENTF_LEFTUP);

    public static void KeyPress(ushort virtualKey)
    {
        // 必须带扫描码，否则 WPF 会把按键判成 ImeProcessed 而不触发 KeyDown
        ushort scan = (ushort)MapVirtualKey(virtualKey, 0);
        SendKey(virtualKey, scan, 0);
        SendKey(virtualKey, scan, KEYEVENTF_KEYUP);
    }

    private static void SendMouse(uint flag)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flag } },
        };

        Send(inputs);
    }

    private static void SendKey(ushort virtualKey, ushort scanCode, uint flag)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = virtualKey, wScan = scanCode, dwFlags = flag },
            },
        };

        Send(inputs);
    }

    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Logger.Warn($"合成输入失败：err={Marshal.GetLastWin32Error()}");
        }
    }
}
