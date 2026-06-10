using System.Runtime.InteropServices;

namespace TinyMouseMacro;

static class Program
{
    private const string MutexName = "TinyMouseMacro_SingleInstance_7a8f291";
    private const int WmShowWindow = NativeMethods.WmUser + 1;

    [STAThread]
    static int Main()
    {
        NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static void ActivateExistingInstance()
    {
        var hwnd = NativeMethods.FindWindowW(null, UiText.AppTitle);
        if (hwnd != 0)
        {
            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.SendMessageW(hwnd, WmShowWindow, 0, 0);
        }
    }
}

internal static partial class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const int WmUser = 0x0400;
    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;
    public const int WmKeyDown = 0x0100;
    public const int WmSysKeyDown = 0x0104;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyUp = 0x0105;
    public const int WmLbuttonDown = 0x0201;
    public const int WmLbuttonUp = 0x0202;
    public const int WmRButtonDown = 0x0204;
    public const int WmMButtonDown = 0x0207;
    public const int WmXButtonDown = 0x020B;
    public const int WmMouseWheel = 0x020A;
    public const int WmMouseMove = 0x0200;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public const uint MouseEventFLeftDown = 0x0002;
    public const uint MouseEventFLeftUp = 0x0004;
    public const uint MouseEventFRightDown = 0x0008;
    public const uint MouseEventFRightUp = 0x0010;
    public const uint MouseEventFMiddleDown = 0x0020;
    public const uint MouseEventFMiddleUp = 0x0040;
    public const uint MouseEventFWheel = 0x0800;
    public const uint KeyeventfKeyup = 0x0002;
    public const uint KeyeventfUnicode = 0x0004;
    public const uint InputKeyboard = 1;

    public delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);
    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, nuint dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] Input[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint SendMessageW(nint hWnd, int Msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(nint hdc, int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msllhookstruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Kbdllhookstruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public Keybdinput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Keybdinput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }
}
