using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class HotkeyService : IDisposable
{
    private readonly nint _windowHandle;
    private readonly Dictionary<int, RegisteredHotkey> _registeredHotkeys = [];
    private readonly Dictionary<int, MacroProfile> _hookKeyProfiles = [];
    private readonly Dictionary<int, MacroProfile> _mouseButtonProfiles = [];
    private int _nextId = 100;
    private nint _keyboardHookHandle;
    private nint _mouseHookHandle;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private readonly HashSet<int> _pressedHookKeys = [];
    private readonly object _hookLock = new();

    public HotkeyService(nint windowHandle)
    {
        _windowHandle = windowHandle;
    }

    public int RegisteredCount => _registeredHotkeys.Count + _hookKeyProfiles.Count + _mouseButtonProfiles.Count;

    public event Action<MacroProfile>? HookTriggered;

    public List<string> RegisterAll(IEnumerable<MacroProfile> profiles)
    {
        Clear();
        var errors = new List<string>();
        var seenKeyboardHotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenHookKeys = new HashSet<int>();
        var seenMouseButtons = new HashSet<int>();

        foreach (var profile in profiles)
        {
            if (!profile.Enabled) continue;

            switch (profile.TriggerType)
            {
                case MacroTriggerType.KeyboardHotkey:
                    errors.AddRange(RegisterKeyboardHotkey(profile, seenKeyboardHotkeys));
                    break;
                case MacroTriggerType.KeyboardHook:
                    errors.AddRange(RegisterKeyboardHook(profile, seenHookKeys));
                    break;
                case MacroTriggerType.MouseButton:
                    errors.AddRange(RegisterMouseButton(profile, seenMouseButtons));
                    break;
            }
        }

        return errors;
    }

    private List<string> RegisterKeyboardHotkey(MacroProfile profile, HashSet<string> seenHotkeys)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Hotkey))
        {
            return errors;
        }

        if (!HotkeyParser.TryParse(profile.Hotkey, out var modifiers, out var key, out var error))
        {
            errors.Add($"{profile.Name}: {error}");
            return errors;
        }

        var normalizedHotkey = HotkeyParser.Normalize(modifiers, key);
        if (!seenHotkeys.Add(normalizedHotkey))
        {
            errors.Add($"{profile.Name}: {UiText.DuplicateHotkey(profile.Hotkey)}");
            return errors;
        }

        var id = _nextId++;
        var registeredHotkey = new RegisteredHotkey(profile, modifiers, key, normalizedHotkey);

        if (NativeMethods.RegisterHotKey(_windowHandle, id, modifiers | NativeMethods.ModNoRepeat, key))
        {
            _registeredHotkeys[id] = registeredHotkey;
        }
        else
        {
            var errorCode = Marshal.GetLastWin32Error();
            var message = new Win32Exception(errorCode).Message;
            errors.Add($"{profile.Name}: {UiText.HotkeyRegisterFailed(profile.Hotkey, message)}");
        }

        return errors;
    }

    private List<string> RegisterKeyboardHook(MacroProfile profile, HashSet<int> seenKeys)
    {
        var errors = new List<string>();
        var keyCode = profile.TriggerKey;

        if (keyCode <= 0)
        {
            return errors;
        }

        if (!seenKeys.Add(keyCode))
        {
            errors.Add($"{profile.Name}: {UiText.DuplicateHookKey(((Keys)keyCode).ToString())}");
            return errors;
        }

        _hookKeyProfiles[keyCode] = profile;
        EnsureKeyboardHook();
        return errors;
    }

    private List<string> RegisterMouseButton(MacroProfile profile, HashSet<int> seenButtons)
    {
        var errors = new List<string>();
        var button = profile.TriggerMouseButton;

        if (button <= 0)
        {
            return errors;
        }

        if (!seenButtons.Add(button))
        {
            errors.Add($"{profile.Name}: {UiText.DuplicateMouseButton(button)}");
            return errors;
        }

        _mouseButtonProfiles[button] = profile;
        EnsureMouseHook();
        return errors;
    }

    private void EnsureKeyboardHook()
    {
        if (_keyboardHookHandle != 0) return;

        _keyboardProc = KeyboardHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandleW(curModule?.ModuleName);
        _keyboardHookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhKeyboardLl, _keyboardProc, moduleHandle, 0);
    }

    private void EnsureMouseHook()
    {
        if (_mouseHookHandle != 0) return;

        _mouseProc = MouseHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandleW(curModule?.ModuleName);
        _mouseHookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhMouseLl, _mouseProc, moduleHandle, 0);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        {
            var kb = Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(lParam);
            var vkCode = (int)kb.VkCode;

            lock (_hookLock)
            {
                if (!_pressedHookKeys.Add(vkCode))
                    return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
            }

            if (_hookKeyProfiles.TryGetValue(vkCode, out var profile))
            {
                Task.Run(() => HookTriggered?.Invoke(profile));
            }
        }
        else if (wParam is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
        {
            var kb = Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(lParam);
            lock (_hookLock)
            {
                _pressedHookKeys.Remove((int)kb.VkCode);
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int? button = wParam switch
            {
                NativeMethods.WmXButtonDown => GetXButton(lParam),
                NativeMethods.WmMButtonDown => 3,
                _ => null
            };

            if (button.HasValue && _mouseButtonProfiles.TryGetValue(button.Value, out var profile))
            {
                Task.Run(() => HookTriggered?.Invoke(profile));
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private static int GetXButton(nint lParam)
    {
        var ms = Marshal.PtrToStructure<NativeMethods.Msllhookstruct>(lParam);
        var xButton = (ms.MouseData >> 16) & 0xFFFF;
        return xButton switch
        {
            1 => 4,
            2 => 5,
            _ => 0
        };
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
        foreach (var id in _registeredHotkeys.Keys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        _registeredHotkeys.Clear();
        _hookKeyProfiles.Clear();
        _mouseButtonProfiles.Clear();

        if (_keyboardHookHandle != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = 0;
        }

        if (_mouseHookHandle != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = 0;
        }

        _keyboardProc = null;
        _mouseProc = null;
    }

    public void Dispose()
    {
        Clear();
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

    public static string FormatModifiers(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.ModWin) != 0) parts.Add("Win");
        return string.Join("+", parts);
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

    public static bool IsModifierKey(Keys key)
    {
        return key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin;
    }
}
