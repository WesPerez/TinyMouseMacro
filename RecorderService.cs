using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class RecorderService : IDisposable
{
    private nint _mouseHookHandle;
    private nint _keyboardHookHandle;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private readonly List<MacroStep> _steps = [];
    private readonly object _lock = new();
    private DateTime _lastMoveTime = DateTime.MinValue;
    private const int MoveThrottleMs = 150;

    public bool IsRecording { get; private set; }
    public IReadOnlyList<MacroStep> Steps
    {
        get { lock (_lock) return _steps.ToList(); }
    }

    public event Action<MacroStep>? StepRecorded;

    public void Start()
    {
        if (IsRecording) return;
        lock (_lock) _steps.Clear();
        IsRecording = true;
        _lastMoveTime = DateTime.MinValue;

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandleW(curModule?.ModuleName);

        _mouseHookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _mouseProc, moduleHandle, 0);
        _keyboardHookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardProc, moduleHandle, 0);
    }

    public List<MacroStep> Stop()
    {
        IsRecording = false;

        if (_mouseHookHandle != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = 0;
        }
        if (_keyboardHookHandle != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = 0;
        }
        _mouseProc = null;
        _keyboardProc = null;

        lock (_lock) return _steps.ToList();
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            var ms = Marshal.PtrToStructure<NativeMethods.Msllhookstruct>(lParam);
            var pt = ms.Pt;

            if (wParam == NativeMethods.WmLbuttonDown)
            {
                AddStep(new MacroStep { Type = MacroStepType.LeftClick, X = pt.X, Y = pt.Y });
            }
            else if (wParam == NativeMethods.WmLbuttonUp)
            {
                // Ignore up events
            }
            else if (wParam == NativeMethods.WmRButtonDown)
            {
                AddStep(new MacroStep { Type = MacroStepType.RightClick, X = pt.X, Y = pt.Y });
            }
            else if (wParam == NativeMethods.WmMButtonDown)
            {
                AddStep(new MacroStep { Type = MacroStepType.MiddleClick, X = pt.X, Y = pt.Y });
            }
            else if (wParam == NativeMethods.WmMouseWheel)
            {
                var delta = (short)((ms.MouseData >> 16) & 0xFFFF);
                AddStep(new MacroStep { Type = MacroStepType.MouseWheel, WheelDelta = delta * 120 });
            }
            else if (wParam == NativeMethods.WmMouseMove)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastMoveTime).TotalMilliseconds >= MoveThrottleMs)
                {
                    _lastMoveTime = now;
                    AddStep(new MacroStep { Type = MacroStepType.Move, X = pt.X, Y = pt.Y });
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && IsRecording && wParam is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        {
            var kb = Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(lParam);
            var vkCode = (int)kb.VkCode;

            if (!IsModifier(vkCode))
            {
                AddStep(new MacroStep { Type = MacroStepType.KeyPress, KeyCode = vkCode });
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private void AddStep(MacroStep step)
    {
        lock (_lock) _steps.Add(step);
        StepRecorded?.Invoke(step);
    }

    private static bool IsModifier(int vk)
    {
        return vk is >= 0xA0 and <= 0xA5 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
    }
}
