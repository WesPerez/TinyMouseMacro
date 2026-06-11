using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class MacroExecutor
{
    private const int MaxExecutedStepsPerRun = 100_000;
    private const uint ClrInvalid = 0xFFFFFFFF;

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
        lock (_lock)
        {
            var id = _pendingChainMacroId;
            _pendingChainMacroId = null;
            return id;
        }
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
            _pendingChainMacroId = null;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = _loopCts.Token;

        return Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(profile, linkedToken);
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
        });
    }

    private async Task RunCoreAsync(MacroProfile profile, CancellationToken ct)
    {
        var repeatCount = profile.RepeatCount;
        var repeatIntervalMs = Math.Max(0, profile.RepeatIntervalMs);
        var isInfinite = repeatCount <= 0;
        var iteration = 0;
        var executedSteps = 0;
        var speedMultiplier = Math.Max(0.1, Math.Min(10.0, profile.SpeedMultiplier));
        var targetWindowTitle = profile.TargetWindowTitle?.Trim();

        var steps = profile.Steps.ToArray();
        while (isInfinite || iteration < repeatCount)
        {
            ct.ThrowIfCancellationRequested();
            FocusTargetWindow(targetWindowTitle);

            for (var index = 0; index < steps.Length; index++)
            {
                ct.ThrowIfCancellationRequested();

                if (++executedSteps > MaxExecutedStepsPerRun)
                {
                    throw new InvalidOperationException($"Macro stopped after {MaxExecutedStepsPerRun:N0} executed steps. Check conditional jumps for an accidental loop.");
                }

                var result = await ExecuteStepAsync(steps[index], speedMultiplier, ct);
                if (result.JumpToIndex.HasValue)
                {
                    var jumpIndex = result.JumpToIndex.Value;
                    if (jumpIndex >= 0 && jumpIndex < steps.Length)
                    {
                        index = jumpIndex - 1;
                    }
                }

                await DelayMs(20, speedMultiplier, ct);
            }

            iteration++;
            if ((isInfinite || iteration < repeatCount) && repeatIntervalMs > 0)
            {
                await DelayMs(repeatIntervalMs, speedMultiplier, ct);
            }
        }
    }

    private static void FocusTargetWindow(string? targetWindowTitle)
    {
        if (string.IsNullOrEmpty(targetWindowTitle)) return;

        var hwnd = NativeMethods.FindWindowW(null, targetWindowTitle);
        if (hwnd != 0)
        {
            NativeMethods.SetForegroundWindow(hwnd);
            Thread.Sleep(50);
        }
    }

    private async Task<StepResult> ExecuteStepAsync(MacroStep step, double speedMultiplier, CancellationToken ct)
    {
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
                await WaitForPixelAsync(step.X, step.Y, step.PixelColor, step.PixelTolerance, step.PixelTimeoutMs, ct);
                break;
            case MacroStepType.FindPixel:
                var found = await FindPixelInRegionAsync(step.X, step.Y, step.SearchWidth, step.SearchHeight, step.PixelColor, step.PixelTolerance, step.PixelTimeoutMs, ct);
                if (found.HasValue)
                    NativeMethods.SetCursorPos(found.Value.x, found.Value.y);
                break;
            case MacroStepType.Screenshot:
                CaptureScreenshot(step.X, step.Y, step.SearchWidth, step.SearchHeight);
                break;
            case MacroStepType.RandomDelay:
                await DelayMs(GetRandomDelay(step.DelayMs, step.DelayMsMax), speedMultiplier, ct);
                break;
            case MacroStepType.JumpIfPixel:
                if (PixelMatches(step.X, step.Y, step.PixelColor, step.PixelTolerance) && step.JumpToStepIndex >= 0)
                {
                    return StepResult.Jump(step.JumpToStepIndex);
                }
                break;
            case MacroStepType.RunProgram:
                RunProgram(step.RunProgramPath, step.RunProgramArgs);
                break;
            case MacroStepType.PlaySound:
                PlaySound(step.SoundFilePath);
                break;
            case MacroStepType.ChainMacro:
                RequestChainMacro(step.ChainMacroId);
                break;
            case MacroStepType.LeftClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFLeftDown, NativeMethods.MouseEventFLeftUp, speedMultiplier, ct);
                break;
            case MacroStepType.RightClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFRightDown, NativeMethods.MouseEventFRightUp, speedMultiplier, ct);
                break;
            case MacroStepType.DoubleClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFLeftDown, NativeMethods.MouseEventFLeftUp, speedMultiplier, ct);
                await DelayMs(50, speedMultiplier, ct);
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFLeftDown, NativeMethods.MouseEventFLeftUp, speedMultiplier, ct);
                break;
            case MacroStepType.MiddleClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFMiddleDown, NativeMethods.MouseEventFMiddleUp, speedMultiplier, ct);
                break;
            case MacroStepType.Delay:
                await DelayMs(Math.Max(0, step.DelayMs), speedMultiplier, ct);
                break;
            case MacroStepType.KeyPress:
                SendKeyPress(step.KeyCode);
                await DelayMs(35, speedMultiplier, ct);
                break;
            case MacroStepType.KeyCombo:
                await SendKeyCombo(step.KeyModifiers, step.KeyCode, speedMultiplier, ct);
                break;
            case MacroStepType.TypeText:
                foreach (var c in step.TextToType ?? string.Empty)
                {
                    ct.ThrowIfCancellationRequested();
                    SendChar(c);
                    await DelayMs(15, speedMultiplier, ct);
                }
                break;
            case MacroStepType.MouseWheel:
                NativeMethods.mouse_event(NativeMethods.MouseEventFWheel, 0, 0, unchecked((uint)step.WheelDelta), 0);
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
        }

        return StepResult.Continue;
    }

    private void RequestChainMacro(string? chainMacroId)
    {
        if (string.IsNullOrWhiteSpace(chainMacroId)) return;

        lock (_lock)
        {
            _pendingChainMacroId = chainMacroId;
        }

        ChainMacroRequested?.Invoke(chainMacroId);
    }

    private static async Task ClickAsync(int x, int y, uint downFlag, uint upFlag, double speedMultiplier, CancellationToken ct)
    {
        NativeMethods.SetCursorPos(x, y);
        await DelayMs(35, speedMultiplier, ct);
        NativeMethods.mouse_event(downFlag, 0, 0, 0, 0);
        await DelayMs(35, speedMultiplier, ct);
        NativeMethods.mouse_event(upFlag, 0, 0, 0, 0);
    }

    private static async Task DelayMs(int ms, double speedMultiplier, CancellationToken ct)
    {
        var adjusted = (int)(Math.Max(0, ms) / speedMultiplier);
        if (adjusted > 0)
            await Task.Delay(adjusted, ct);
    }

    private static int GetRandomDelay(int first, int second)
    {
        var min = Math.Max(0, Math.Min(first, second));
        var max = Math.Max(0, Math.Max(first, second));
        if (max <= min) return min;
        if (max == int.MaxValue) return Random.Shared.Next(min, max);
        return Random.Shared.Next(min, max + 1);
    }

    private static void SendKeyPress(int keyCode)
    {
        if (keyCode is <= 0 or > byte.MaxValue) return;

        var key = (byte)keyCode;
        NativeMethods.keybd_event(key, 0, 0, 0);
        NativeMethods.keybd_event(key, 0, NativeMethods.KeyeventfKeyup, 0);
    }

    private static async Task SendKeyCombo(uint modifiers, int keyCode, double speedMultiplier, CancellationToken ct)
    {
        if (keyCode is <= 0 or > byte.MaxValue) return;

        var key = (byte)keyCode;
        if ((modifiers & NativeMethods.ModControl) != 0)
            NativeMethods.keybd_event((byte)Keys.ControlKey, 0, 0, 0);
        if ((modifiers & NativeMethods.ModAlt) != 0)
            NativeMethods.keybd_event((byte)Keys.Menu, 0, 0, 0);
        if ((modifiers & NativeMethods.ModShift) != 0)
            NativeMethods.keybd_event((byte)Keys.ShiftKey, 0, 0, 0);
        if ((modifiers & NativeMethods.ModWin) != 0)
            NativeMethods.keybd_event((byte)Keys.LWin, 0, 0, 0);

        NativeMethods.keybd_event(key, 0, 0, 0);
        await DelayMs(35, speedMultiplier, ct);
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
        if (timeoutMs <= 0) return;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (PixelMatches(x, y, targetColor, tolerance))
                return;

            await Task.Delay(50, ct);
        }
    }

    private static async Task<(int x, int y)?> FindPixelInRegionAsync(int startX, int startY, int width, int height, int targetColor, int tolerance, int timeoutMs, CancellationToken ct)
    {
        if (width <= 0 || height <= 0) return null;

        if (timeoutMs <= 0)
        {
            return FindPixelOnce(startX, startY, width, height, targetColor, tolerance);
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            var found = FindPixelOnce(startX, startY, width, height, targetColor, tolerance);
            if (found.HasValue)
                return found;

            await Task.Delay(100, ct);
        }

        return null;
    }

    private static (int x, int y)? FindPixelOnce(int startX, int startY, int width, int height, int targetColor, int tolerance)
    {
        var dc = NativeMethods.GetDC(0);
        if (dc == 0) return null;

        try
        {
            for (var dy = 0; dy < height; dy++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    var pixel = NativeMethods.GetPixel(dc, startX + dx, startY + dy);
                    if (pixel != ClrInvalid && ColorMatch((int)pixel, targetColor, tolerance))
                        return (startX + dx, startY + dy);
                }
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(0, dc);
        }

        return null;
    }

    private static bool PixelMatches(int x, int y, int targetColor, int tolerance)
    {
        var dc = NativeMethods.GetDC(0);
        if (dc == 0) return false;

        try
        {
            var pixel = NativeMethods.GetPixel(dc, x, y);
            return pixel != ClrInvalid && ColorMatch((int)pixel, targetColor, tolerance);
        }
        finally
        {
            NativeMethods.ReleaseDC(0, dc);
        }
    }

    private static void CaptureScreenshot(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 7680 || height > 4320) return;

        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TinyMouseMacro");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RunProgram(string? path, string? args)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var psi = new ProcessStartInfo(path, args ?? string.Empty)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(psi);
        }
        catch
        {
        }
    }

    private static void PlaySound(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            using var player = new System.Media.SoundPlayer(path);
            player.Play();
        }
        catch
        {
        }
    }

    private static bool ColorMatch(int pixel, int target, int tolerance)
    {
        var dr = Math.Abs(((pixel >> 16) & 0xFF) - ((target >> 16) & 0xFF));
        var dg = Math.Abs(((pixel >> 8) & 0xFF) - ((target >> 8) & 0xFF));
        var db = Math.Abs((pixel & 0xFF) - (target & 0xFF));
        return dr <= tolerance && dg <= tolerance && db <= tolerance;
    }

    private readonly record struct StepResult(int? JumpToIndex)
    {
        public static StepResult Continue { get; } = new(null);
        public static StepResult Jump(int index) => new(index);
    }
}
