using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class HotkeyService : IDisposable
{
    private readonly nint _windowHandle;
    private readonly Dictionary<int, RegisteredHotkey> _registeredHotkeys = [];
    private readonly List<RegisteredHotkey> _fallbackHotkeys = [];
    private readonly Dictionary<string, DateTime> _lastTriggers = [];
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 35 };
    private NativeMethods.LowLevelKeyboardProc? _keyboardCallback;
    private nint _keyboardHookHandle;
    private int _nextId = 100;

    public event EventHandler<MacroProfile>? HotkeyPressed;

    public HotkeyService(nint windowHandle)
    {
        _windowHandle = windowHandle;
        _pollTimer.Tick += (_, _) => PollHotkeys();
    }

    public int RegisteredCount => _registeredHotkeys.Count + _fallbackHotkeys.Count;

    public List<string> RegisterAll(IEnumerable<MacroProfile> profiles)
    {
        Clear();
        var errors = new List<string>();
        var seenHotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            if (!HotkeyParser.TryParse(profile.Hotkey, out var modifiers, out var key, out var error))
            {
                errors.Add($"{profile.Name}: {error}");
                continue;
            }

            var normalizedHotkey = HotkeyParser.Normalize(modifiers, key);
            if (!seenHotkeys.Add(normalizedHotkey))
            {
                errors.Add($"{profile.Name}: {UiText.DuplicateHotkey(profile.Hotkey)}");
                continue;
            }

            var id = _nextId++;
            var registeredHotkey = new RegisteredHotkey(profile, modifiers, key, normalizedHotkey);

            if (NativeMethods.RegisterHotKey(_windowHandle, id, modifiers | NativeMethods.ModNoRepeat, key))
            {
                _registeredHotkeys[id] = registeredHotkey;
            }
            else
            {
                var message = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                errors.Add($"{profile.Name}: {UiText.HotkeyRegisterFailed(profile.Hotkey, message)}");
                _fallbackHotkeys.Add(registeredHotkey);
            }
        }

        if (_fallbackHotkeys.Count > 0)
        {
            EnsureKeyboardHook();
        }

        _pollTimer.Enabled = _fallbackHotkeys.Count > 0;
        return errors;
    }

    public bool TryGetProfile(int hotkeyId, out MacroProfile? profile)
    {
        if (_registeredHotkeys.TryGetValue(hotkeyId, out var hotkey))
        {
            profile = hotkey.Profile;
            return true;
        }

        profile = null;
        return false;
    }

    public void Clear()
    {
        _pollTimer.Enabled = false;

        foreach (var id in _registeredHotkeys.Keys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        _registeredHotkeys.Clear();
        _fallbackHotkeys.Clear();
        _lastTriggers.Clear();
        StopKeyboardHook();
    }

    public void Dispose()
    {
        Clear();
        _pollTimer.Dispose();
    }

    private void EnsureKeyboardHook()
    {
        if (_keyboardHookHandle != 0 || _fallbackHotkeys.Count == 0)
        {
            return;
        }

        _keyboardCallback = KeyboardHookCallback;
        var moduleHandle = NativeMethods.GetModuleHandleW(null);
        _keyboardHookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardCallback, moduleHandle, 0);
    }

    private void StopKeyboardHook()
    {
        if (_keyboardHookHandle != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = 0;
        }

        _keyboardCallback = null;
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == NativeMethods.WmKeyDown || wParam == NativeMethods.WmSysKeyDown))
        {
            var info = Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(lParam);
            foreach (var hotkey in _fallbackHotkeys)
            {
                if (hotkey.Key == info.VkCode && ModifiersMatch(hotkey.Modifiers))
                {
                    Trigger(hotkey);
                    break;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private void PollHotkeys()
    {
        foreach (var hotkey in _fallbackHotkeys)
        {
            if (IsKeyDown((Keys)hotkey.Key) && ModifiersMatch(hotkey.Modifiers))
            {
                Trigger(hotkey);
            }
        }
    }

    private void Trigger(RegisteredHotkey hotkey)
    {
        var key = hotkey.Profile.Id;
        var now = DateTime.UtcNow;

        if (_lastTriggers.TryGetValue(key, out var last) && now - last < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        _lastTriggers[key] = now;
        HotkeyPressed?.Invoke(this, hotkey.Profile);
    }

    private static bool ModifiersMatch(uint modifiers)
    {
        return ModifierMatches(modifiers, NativeMethods.ModAlt, Keys.Menu, Keys.LMenu, Keys.RMenu)
            && ModifierMatches(modifiers, NativeMethods.ModControl, Keys.ControlKey, Keys.LControlKey, Keys.RControlKey)
            && ModifierMatches(modifiers, NativeMethods.ModShift, Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey)
            && ModifierMatches(modifiers, NativeMethods.ModWin, Keys.LWin, Keys.RWin);
    }

    private static bool ModifierMatches(uint modifiers, uint modifier, params Keys[] keys)
    {
        var expected = (modifiers & modifier) != 0;
        var actual = keys.Any(IsKeyDown);
        return expected == actual;
    }

    private static bool IsKeyDown(Keys key)
    {
        return (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0;
    }

    private sealed record RegisteredHotkey(MacroProfile Profile, uint Modifiers, uint Key, string NormalizedHotkey);
}

public static class HotkeyParser
{
    private static readonly Dictionary<string, Keys> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ESC"] = Keys.Escape,
        ["ESCAPE"] = Keys.Escape,
        ["PLUS"] = Keys.Oemplus,
        ["MINUS"] = Keys.OemMinus,
        ["COMMA"] = Keys.Oemcomma,
        ["PERIOD"] = Keys.OemPeriod,
        ["DOT"] = Keys.OemPeriod,
        ["SPACE"] = Keys.Space,
        ["SPACEBAR"] = Keys.Space,
        ["ENTER"] = Keys.Enter,
        ["RETURN"] = Keys.Return,
        ["TAB"] = Keys.Tab,
        ["BACKSPACE"] = Keys.Back,
        ["DELETE"] = Keys.Delete,
        ["DEL"] = Keys.Delete,
        ["INSERT"] = Keys.Insert,
        ["INS"] = Keys.Insert,
        ["HOME"] = Keys.Home,
        ["END"] = Keys.End,
        ["PAGEUP"] = Keys.PageUp,
        ["PAGEDOWN"] = Keys.PageDown,
        ["PGUP"] = Keys.PageUp,
        ["PGDN"] = Keys.PageDown,
        ["UP"] = Keys.Up,
        ["DOWN"] = Keys.Down,
        ["LEFT"] = Keys.Left,
        ["RIGHT"] = Keys.Right
    };

    public static bool TryParse(string text, out uint modifiers, out uint key, out string error)
    {
        modifiers = 0;
        key = 0;
        error = string.Empty;

        var parts = text
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => part.Length > 0)
            .ToArray();

        if (parts.Length == 0)
        {
            error = UiText.HotkeyEmpty;
            return false;
        }

        Keys? mainKey = null;
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "ALT":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    if (mainKey.HasValue)
                    {
                        error = UiText.HotkeyOnlyOneMainKey;
                        return false;
                    }

                    if (!TryParseKey(part, out var parsedKey))
                    {
                        error = UiText.HotkeyUnknownKey(part);
                        return false;
                    }

                    mainKey = parsedKey;
                    break;
            }
        }

        if (!mainKey.HasValue)
        {
            error = UiText.HotkeyNeedsMainKey;
            return false;
        }

        key = (uint)mainKey.Value;
        return true;
    }

    public static string FromKeyEvent(KeyEventArgs e)
    {
        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        if ((NativeMethods.GetAsyncKeyState((int)Keys.LWin) & 0x8000) != 0 ||
            (NativeMethods.GetAsyncKeyState((int)Keys.RWin) & 0x8000) != 0)
        {
            parts.Add("Win");
        }

        var keyCode = e.KeyCode;
        if (!IsModifierKey(keyCode))
        {
            parts.Add(FormatKey(keyCode));
        }

        return string.Join("+", parts);
    }

    public static string Normalize(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.ModWin) != 0) parts.Add("Win");
        parts.Add(FormatKey((Keys)key));
        return string.Join("+", parts).ToUpperInvariant();
    }

    private static bool TryParseKey(string text, out Keys key)
    {
        key = Keys.None;

        if (KeyAliases.TryGetValue(text, out key))
        {
            return true;
        }

        if (text.Length == 1)
        {
            var c = char.ToUpperInvariant(text[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = Keys.A + (c - 'A');
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                key = Keys.D0 + (c - '0');
                return true;
            }
        }

        return Enum.TryParse(text, ignoreCase: true, out key) && key != Keys.None;
    }

    private static string FormatKey(Keys key)
    {
        if (key is >= Keys.A and <= Keys.Z)
        {
            return key.ToString();
        }

        if (key is >= Keys.D0 and <= Keys.D9)
        {
            return ((int)(key - Keys.D0)).ToString();
        }

        return key switch
        {
            Keys.Escape => "Esc",
            Keys.Space => "Space",
            Keys.Return => "Enter",
            Keys.Menu => "Alt",
            Keys.ControlKey => "Ctrl",
            Keys.ShiftKey => "Shift",
            _ => key.ToString()
        };
    }

    private static bool IsModifierKey(Keys key)
    {
        return key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin;
    }
}
