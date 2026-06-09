using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class MouseRecorder : IDisposable
{
    private NativeMethods.LowLevelMouseProc? _callback;
    private nint _hookHandle;
    private TaskCompletionSource<Point>? _pendingCapture;

    public bool IsCapturing => _pendingCapture is not null;

    public Task<Point> CaptureNextLeftClickAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingCapture is not null)
        {
            return _pendingCapture.Task;
        }

        _pendingCapture = new TaskCompletionSource<Point>(TaskCreationOptions.RunContinuationsAsynchronously);
        _callback = HookCallback;

        var moduleHandle = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _callback, moduleHandle, 0);
        if (_hookHandle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _pendingCapture = null;
            _callback = null;
            throw new InvalidOperationException($"Failed to install mouse hook. Win32 error: {error}");
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(CancelCapture);
        }

        return _pendingCapture.Task;
    }

    public void CancelCapture()
    {
        var pendingCapture = _pendingCapture;
        if (pendingCapture is null)
        {
            return;
        }

        StopHook();
        pendingCapture.TrySetCanceled();
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam == NativeMethods.WmLbuttonDown && _pendingCapture is not null)
        {
            var info = Marshal.PtrToStructure<NativeMethods.Msllhookstruct>(lParam);
            var point = new Point(info.Pt.X, info.Pt.Y);
            var pendingCapture = _pendingCapture;

            StopHook();
            pendingCapture.TrySetResult(point);
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void StopHook()
    {
        if (_hookHandle != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }

        _pendingCapture = null;
        _callback = null;
    }

    public void Dispose()
    {
        StopHook();
    }
}
