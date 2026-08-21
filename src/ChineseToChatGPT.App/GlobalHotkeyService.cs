using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

internal sealed class GlobalHotkeyService : IHotkeyService
{
    private const int HotkeyId = 0x4347;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private nint _handle;

    public event EventHandler? Pressed;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(WndProc);
    }

    public void Register(string gesture)
    {
        if (_handle == 0)
        {
            throw new InvalidOperationException("The hotkey window is not ready.");
        }

        Unregister();
        var parsed = HotkeyGesture.Parse(gesture);
        if (!RegisterHotKey(_handle, HotkeyId, parsed.Modifiers | 0x4000u, parsed.VirtualKey))
        {
            throw new CompanionException(
                ErrorCategory.HotkeyConflict,
                $"The hotkey '{gesture}' is already in use.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    public void Unregister()
    {
        if (_handle != 0)
        {
            UnregisterHotKey(_handle, HotkeyId);
        }
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    private sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
    {
        public static HotkeyGesture Parse(string value)
        {
            var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new FormatException("Use a gesture such as Ctrl+Shift+Enter.");
            }

            uint modifiers = 0;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                modifiers |= parts[index].ToUpperInvariant() switch
                {
                    "CTRL" or "CONTROL" => 0x0002u,
                    "SHIFT" => 0x0004u,
                    "ALT" => 0x0001u,
                    "WIN" or "WINDOWS" => 0x0008u,
                    _ => throw new FormatException($"Unknown modifier '{parts[index]}'.")
                };
            }

            var key = (Key)new KeyConverter().ConvertFromInvariantString(parts[^1])!;
            var virtualKey = checked((uint)KeyInterop.VirtualKeyFromKey(key));
            if (virtualKey == 0)
            {
                throw new FormatException($"Unknown key '{parts[^1]}'.");
            }

            return new HotkeyGesture(modifiers, virtualKey);
        }
    }
}
