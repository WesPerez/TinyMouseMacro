namespace TinyMouseMacro;

public sealed class MacroExecutor
{
    private readonly object _lock = new();
    private bool _isRunning;

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

        return Task.Run(async () =>
        {
            try
            {
                foreach (var step in profile.Steps.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    switch (step.Type)
                    {
                        case MacroStepType.Move:
                            NativeMethods.SetCursorPos(step.X, step.Y);
                            break;
                        case MacroStepType.LeftClick:
                            NativeMethods.SetCursorPos(step.X, step.Y);
                            await Task.Delay(35, cancellationToken);
                            NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, 0);
                            await Task.Delay(35, cancellationToken);
                            NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, 0);
                            break;
                        case MacroStepType.Delay:
                            await Task.Delay(Math.Max(0, step.DelayMs), cancellationToken);
                            break;
                    }

                    await Task.Delay(20, cancellationToken);
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isRunning = false;
                }
            }
        }, cancellationToken);
    }
}
