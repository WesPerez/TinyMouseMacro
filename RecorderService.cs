using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class RecorderService : IDisposable
{
    private const int MoveThrottleMs = 150;

    private nint _mouseHookHandle;
    private nint _keyboardHookHandle;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private readonly List<MacroStep> _steps = [];
    private readonly object _lock = new();
    private DateTime _lastMoveTime = DateTime.MinValue;
    private volatile bool _isRecording;

    public bool IsRecording => _isRecording;

    public IReadOnlyList<MacroStep> Steps
    {
        get { lock (_lock) return _steps.ToList(); }
    }

    public event Action<MacroStep>? StepRecorded;

    public void Start()
    {
        if (_isRecording) return;

        lock (_lock) _steps.Clear();
        _isRecording = true;
        _lastMoveTime = DateTime.MinValue;

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandleW(curModule?.ModuleName);

        _mouseHookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _mouseProc, moduleHandle, 0);
        if (_mouseHookHandle == 0)
        {
            ThrowStartFailed(UiText.MouseHook, Marshal.GetLastWin32Error());
        }

        _keyboardHookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardProc, moduleHandle, 0);
        if (_keyboardHookHandle == 0)
        {
            ThrowStartFailed(UiText.KeyboardHook, Marshal.GetLastWin32Error());
        }
    }

    public List<MacroStep> Stop()
    {
        _isRecording = false;

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
        if (nCode >= 0 && _isRecording)
        {
            var ms = Marshal.PtrToStructure<NativeMethods.Msllhookstruct>(lParam);
            var pt = ms.Pt;

            if (wParam == NativeMethods.WmLbuttonDown)
            {
                AddStep(new MacroStep { Type = MacroStepType.LeftClick, X = pt.X, Y = pt.Y });
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
                AddStep(new MacroStep { Type = MacroStepType.MouseWheel, WheelDelta = delta });
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
        if (nCode >= 0 && _isRecording && wParam is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
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

        try
        {
            StepRecorded?.Invoke(step);
        }
        catch
        {
        }
    }

    private void ThrowStartFailed(string hookName, int errorCode)
    {
        Stop();
        var message = new Win32Exception(errorCode).Message;
        throw new InvalidOperationException(UiText.HookInstallFailed(hookName, message));
    }

    private static bool IsModifier(int vk)
    {
        return vk is >= 0xA0 and <= 0xA5 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
    }

    public void Dispose()
    {
        if (_isRecording) Stop();
    }
}
