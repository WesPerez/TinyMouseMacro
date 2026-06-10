using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class MacroExecutor
{
    private readonly object _lock = new();
    private bool _isRunning;
    private CancellationTokenSource? _loopCts;
    private string? _pendingChainMacroId;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _isRunning;
            }
        }
    }

    public void Cancel()
    {
        _loopCts?.Cancel();
    }

    public event Action<string>? ChainMacroRequested;

    public string? ConsumeChainMacroId()
    {
        var id = _pendingChainMacroId;
        _pendingChainMacroId = null;
        return id;
    }

    public Task RunAsync(MacroProfile profile, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return Task.CompletedTask;
            }

            _isRunning = true;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = _loopCts.Token;

        return Task.Run(async () =>
        {
            try
            {
                var repeatCount = profile.RepeatCount;
                var repeatIntervalMs = Math.Max(0, profile.RepeatIntervalMs);
                var isInfinite = repeatCount <= 0;
                var iteration = 0;
                var speedMultiplier = Math.Max(0.1, Math.Min(10.0, profile.SpeedMultiplier));
                var targetWindowTitle = profile.TargetWindowTitle?.Trim();

                while (isInfinite || iteration < repeatCount)
                {
                    linkedToken.ThrowIfCancellationRequested();

                    if (!string.IsNullOrEmpty(targetWindowTitle))
                    {
                        var hwnd = NativeMethods.FindWindowW(null, targetWindowTitle);
                        if (hwnd != 0)
                        {
                            NativeMethods.SetForegroundWindow(hwnd);
                            await Task.Delay(50, linkedToken);
                        }
                    }

                    foreach (var step in profile.Steps.ToArray())
                    {
                        linkedToken.ThrowIfCancellationRequested();

                        switch (step.Type)
                        {
                            case MacroStepType.Move:
                                NativeMethods.SetCursorPos(step.X, step.Y);
                                break;
                            case MacroStepType.MoveRelative:
                                var currentPos = Cursor.Position;
                                NativeMethods.SetCursorPos(currentPos.X + step.X, currentPos.Y + step.Y);
                                break;
                            case MacroStepType.WaitPixel:
                                await WaitForPixelAsync(step.X, step.Y, step.PixelColor, step.PixelTolerance, step.PixelTimeoutMs, linkedToken);
                                break;
                            case MacroStepType.FindPixel:
                                var found = await FindPixelInRegionAsync(step.X, step.Y, step.SearchWidth, step.SearchHeight, step.PixelColor, step.PixelTolerance, step.PixelTimeoutMs, linkedToken);
                                if (found.HasValue)
                                    NativeMethods.SetCursorPos(found.Value.x, found.Value.y);
                                break;
                            case MacroStepType.Screenshot:
                                await CaptureScreenshotAsync(step.X, step.Y, step.SearchWidth, step.SearchHeight, linkedToken);
                                break;
                            case MacroStepType.RandomDelay:
                                var randomMs = Random.Shared.Next(Math.Min(step.DelayMs, step.DelayMsMax), Math.Max(step.DelayMs, step.DelayMsMax) + 1);
                                await DelayMs(randomMs, speedMultiplier, linkedToken);
                                break;
                            case MacroStepType.JumpIfPixel:
                                var dc2 = NativeMethods.GetDC(0);
                                try
                                {
                                    var px = (int)NativeMethods.GetPixel(dc2, step.X, step.Y);
                                    if (ColorMatch(px, step.PixelColor, step.PixelTolerance) && step.JumpToStepIndex >= 0)
                                    {
                                        // We'll handle this by re-executing the step list from the jump index
                                        await ExecuteJump(profile, step.JumpToStepIndex, speedMultiplier, linkedToken);
                                        return;
                                    }
                                }
                                finally
                                {
                                    NativeMethods.ReleaseDC(0, dc2);
                                }
                                break;
                            case MacroStepType.RunProgram:
                                try
                                {
                                    var psi = new ProcessStartInfo(step.RunProgramPath, step.RunProgramArgs)
                                    {
                                        UseShellExecute = true,
                                        WindowStyle = ProcessWindowStyle.Normal
                                    };
                                    Process.Start(psi);
                                }
                                catch { }
                                break;
                            case MacroStepType.PlaySound:
                                if (File.Exists(step.SoundFilePath))
                                {
                                    try
                                    {
                                        using var player = new System.Media.SoundPlayer(step.SoundFilePath);
                                        player.Play();
                                    }
                                    catch { }
                                }
                                break;
                            case MacroStepType.ChainMacro:
                                if (!string.IsNullOrEmpty(step.ChainMacroId))
                                {
                                    _pendingChainMacroId = step.ChainMacroId;
                                    ChainMacroRequested?.Invoke(step.ChainMacroId);
                                }
                                break;
                            case MacroStepType.LeftClick:
                                NativeMethods.SetCursorPos(step.X, step.Y);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                                break;
                            case MacroStepType.RightClick:
                                NativeMethods.SetCursorPos(step.X, step.Y);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFRightDown, 0, 0, 0, 0);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFRightUp, 0, 0, 0, 0);
                                break;
                            case MacroStepType.DoubleClick:
                                NativeMethods.SetCursorPos(step.X, step.Y);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                                await DelayMs(50, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                                break;
                            case MacroStepType.MiddleClick:
                                NativeMethods.SetCursorPos(step.X, step.Y);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFMiddleDown, 0, 0, 0, 0);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFMiddleUp, 0, 0, 0, 0);
                                break;
                            case MacroStepType.Delay:
                                await DelayMs(Math.Max(0, step.DelayMs), speedMultiplier, linkedToken);
                                break;
                            case MacroStepType.KeyPress:
                                NativeMethods.keybd_event((byte)step.KeyCode, 0, 0, 0);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.keybd_event((byte)step.KeyCode, 0, NativeMethods.KeyeventfKeyup, 0);
                                break;
                            case MacroStepType.KeyCombo:
                                SendKeyCombo(step.KeyModifiers, (byte)step.KeyCode);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                break;
                            case MacroStepType.TypeText:
                                foreach (var c in step.TextToType)
                                {
                                    linkedToken.ThrowIfCancellationRequested();
                                    SendChar(c);
                                    await DelayMs(15, speedMultiplier, linkedToken);
                                }
                                break;
                            case MacroStepType.MouseWheel:
                                NativeMethods.mouse_event(NativeMethods.MouseEventFWheel, 0, 0, (uint)step.WheelDelta, 0);
                                break;
                            case MacroStepType.Drag:
                                NativeMethods.SetCursorPos(step.X, step.Y);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.SetCursorPos(step.DragEndX, step.DragEndY);
                                await DelayMs(35, speedMultiplier, linkedToken);
                                NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                                break;
                        }

                        await DelayMs(20, speedMultiplier, linkedToken);
                    }

                    iteration++;
                    if ((isInfinite || iteration < repeatCount) && repeatIntervalMs > 0)
                    {
                        await DelayMs(repeatIntervalMs, speedMultiplier, linkedToken);
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isRunning = false;
                }

                _loopCts?.Dispose();
                _loopCts = null;
            }
        }, linkedToken);
    }

    private static async Task DelayMs(int ms, double speedMultiplier, CancellationToken ct)
    {
        var adjusted = (int)(ms / speedMultiplier);
        if (adjusted > 0)
            await Task.Delay(adjusted, ct);
    }

    private static void SendKeyCombo(uint modifiers, byte key)
    {
        if ((modifiers & NativeMethods.ModControl) != 0)
            NativeMethods.keybd_event((byte)Keys.ControlKey, 0, 0, 0);
        if ((modifiers & NativeMethods.ModAlt) != 0)
            NativeMethods.keybd_event((byte)Keys.Menu, 0, 0, 0);
        if ((modifiers & NativeMethods.ModShift) != 0)
            NativeMethods.keybd_event((byte)Keys.ShiftKey, 0, 0, 0);
        if ((modifiers & NativeMethods.ModWin) != 0)
            NativeMethods.keybd_event((byte)Keys.LWin, 0, 0, 0);

        NativeMethods.keybd_event(key, 0, 0, 0);
        Thread.Sleep(35);
        NativeMethods.keybd_event(key, 0, NativeMethods.KeyeventfKeyup, 0);

        if ((modifiers & NativeMethods.ModWin) != 0)
            NativeMethods.keybd_event((byte)Keys.LWin, 0, NativeMethods.KeyeventfKeyup, 0);
        if ((modifiers & NativeMethods.ModShift) != 0)
            NativeMethods.keybd_event((byte)Keys.ShiftKey, 0, NativeMethods.KeyeventfKeyup, 0);
        if ((modifiers & NativeMethods.ModAlt) != 0)
            NativeMethods.keybd_event((byte)Keys.Menu, 0, NativeMethods.KeyeventfKeyup, 0);
        if ((modifiers & NativeMethods.ModControl) != 0)
            NativeMethods.keybd_event((byte)Keys.ControlKey, 0, NativeMethods.KeyeventfKeyup, 0);
    }

    private static void SendChar(char c)
    {
        var inputs = new NativeMethods.Input[2];

        inputs[0] = new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.Keybdinput
                {
                    WVk = 0,
                    WScan = c,
                    DwFlags = NativeMethods.KeyeventfUnicode,
                    Time = 0,
                    DwExtraInfo = 0
                }
            }
        };

        inputs[1] = new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.Keybdinput
                {
                    WVk = 0,
                    WScan = c,
                    DwFlags = NativeMethods.KeyeventfUnicode | NativeMethods.KeyeventfKeyup,
                    Time = 0,
                    DwExtraInfo = 0
                }
            }
        };

        NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.Input>());
    }

    private static async Task WaitForPixelAsync(int x, int y, int targetColor, int tolerance, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var dc = NativeMethods.GetDC(0);
            try
            {
                var pixel = (int)NativeMethods.GetPixel(dc, x, y);
                if (ColorMatch(pixel, targetColor, tolerance))
                    return;
            }
            finally
            {
                NativeMethods.ReleaseDC(0, dc);
            }
            await Task.Delay(50, ct);
        }
    }

    private static async Task<(int x, int y)?> FindPixelInRegionAsync(int startX, int startY, int width, int height, int targetColor, int tolerance, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var dc = NativeMethods.GetDC(0);
            try
            {
                for (var dy = 0; dy < height; dy++)
                {
                    for (var dx = 0; dx < width; dx++)
                    {
                        var pixel = (int)NativeMethods.GetPixel(dc, startX + dx, startY + dy);
                        if (ColorMatch(pixel, targetColor, tolerance))
                            return (startX + dx, startY + dy);
                    }
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(0, dc);
            }
            await Task.Delay(100, ct);
        }
        return null;
    }

    private static async Task CaptureScreenshotAsync(int x, int y, int width, int height, CancellationToken ct)
    {
        if (width <= 0 || height <= 0) return;
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TinyMouseMacro");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        await Task.CompletedTask;
    }

    private static bool ColorMatch(int pixel, int target, int tolerance)
    {
        var dr = Math.Abs(((pixel >> 16) & 0xFF) - ((target >> 16) & 0xFF));
        var dg = Math.Abs(((pixel >> 8) & 0xFF) - ((target >> 8) & 0xFF));
        var db = Math.Abs((pixel & 0xFF) - (target & 0xFF));
        return dr <= tolerance && dg <= tolerance && db <= tolerance;
    }

    private static async Task ExecuteJump(MacroProfile profile, int jumpIndex, double speedMultiplier, CancellationToken ct)
    {
        var steps = profile.Steps;
        if (jumpIndex < 0 || jumpIndex >= steps.Count) return;

        for (var i = jumpIndex; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];

            switch (step.Type)
            {
                case MacroStepType.Move:
                    NativeMethods.SetCursorPos(step.X, step.Y);
                    break;
                case MacroStepType.MoveRelative:
                    var cp = Cursor.Position;
                    NativeMethods.SetCursorPos(cp.X + step.X, cp.Y + step.Y);
                    break;
                case MacroStepType.LeftClick:
                    NativeMethods.SetCursorPos(step.X, step.Y);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                    break;
                case MacroStepType.RightClick:
                    NativeMethods.SetCursorPos(step.X, step.Y);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFRightDown, 0, 0, 0, 0);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFRightUp, 0, 0, 0, 0);
                    break;
                case MacroStepType.DoubleClick:
                    NativeMethods.SetCursorPos(step.X, step.Y);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                    await DelayMs(50, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                    break;
                case MacroStepType.MiddleClick:
                    NativeMethods.SetCursorPos(step.X, step.Y);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFMiddleDown, 0, 0, 0, 0);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFMiddleUp, 0, 0, 0, 0);
                    break;
                case MacroStepType.Delay:
                    await DelayMs(Math.Max(0, step.DelayMs), speedMultiplier, ct);
                    break;
                case MacroStepType.RandomDelay:
                    var rms = Random.Shared.Next(Math.Min(step.DelayMs, step.DelayMsMax), Math.Max(step.DelayMs, step.DelayMsMax) + 1);
                    await DelayMs(rms, speedMultiplier, ct);
                    break;
                case MacroStepType.KeyPress:
                    NativeMethods.keybd_event((byte)step.KeyCode, 0, 0, 0);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.keybd_event((byte)step.KeyCode, 0, NativeMethods.KeyeventfKeyup, 0);
                    break;
                case MacroStepType.KeyCombo:
                    SendKeyCombo(step.KeyModifiers, (byte)step.KeyCode);
                    await DelayMs(35, speedMultiplier, ct);
                    break;
                case MacroStepType.TypeText:
                    foreach (var c in step.TextToType)
                    {
                        ct.ThrowIfCancellationRequested();
                        SendChar(c);
                        await DelayMs(15, speedMultiplier, ct);
                    }
                    break;
                case MacroStepType.MouseWheel:
                    NativeMethods.mouse_event(NativeMethods.MouseEventFWheel, 0, 0, (uint)step.WheelDelta, 0);
                    break;
                case MacroStepType.Drag:
                    NativeMethods.SetCursorPos(step.X, step.Y);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.SetCursorPos(step.DragEndX, step.DragEndY);
                    await DelayMs(35, speedMultiplier, ct);
                    NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                    break;
                case MacroStepType.RunProgram:
                    try
                    {
                        var psi = new ProcessStartInfo(step.RunProgramPath, step.RunProgramArgs)
                        {
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal
                        };
                        Process.Start(psi);
                    }
                    catch { }
                    break;
                case MacroStepType.PlaySound:
                    if (File.Exists(step.SoundFilePath))
                    {
                        try
                        {
                            using var player = new System.Media.SoundPlayer(step.SoundFilePath);
                            player.Play();
                        }
                        catch { }
                    }
                    break;
            }

            await DelayMs(20, speedMultiplier, ct);
        }
    }
}
