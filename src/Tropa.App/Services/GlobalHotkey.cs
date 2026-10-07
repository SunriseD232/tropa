using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Tropa.App.Services;

/// <summary>
/// Глобальная горячая клавиша (info.ru.json: hotkey) через RegisterHotKey. Работает и когда окно
/// скрыто в трей: сообщение WM_HOTKEY приходит в окно Тропы. Если сочетание уже занято другой
/// программой, Windows его не отдаст — об этом сообщает <see cref="Register"/>.
/// </summary>
internal sealed partial class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x7470; // «tp»
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly Window _window;
    private readonly Action _pressed;
    private IntPtr _hwnd;
    private bool _registered;

    public GlobalHotkey(Window window, Action pressed)
    {
        _window = window;
        _pressed = pressed;
        Win32Properties.AddWndProcHookCallback(window, WndProc);
    }

    /// <summary>null — успешно (или клавиша выключена пустой строкой), иначе причина.</summary>
    public string? Register(string? spec)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(spec))
            return null;
        if (!TryParse(spec, out var modifiers, out var key))
            return "Не удалось разобрать сочетание клавиш.";
        _hwnd = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hwnd == IntPtr.Zero)
            return "Окно ещё не создано.";
        _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers | ModNoRepeat, key);
        return _registered ? null : $"Сочетание {spec} уже занято другой программой.";
    }

    internal static bool TryParse(string spec, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL": modifiers |= ModControl; break;
                case "ALT": modifiers |= ModAlt; break;
                case "SHIFT": modifiers |= ModShift; break;
                case "WIN": modifiers |= ModWin; break;
                default:
                    if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
                        key = part[0];
                    else if (part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 12)
                        key = (uint)(0x70 + f - 1);
                    else
                        return false;
                    break;
            }
        }

        return modifiers != 0 && key != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            _pressed();
        }

        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (_registered)
            UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    public void Dispose() => Unregister();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
