using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickSearch.App.Services;

public class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9000;

    private readonly HwndSource _source;
    private bool _isRegistered;

    public event EventHandler? HotkeyPressed;

    public HotkeyManager(HwndSource source)
    {
        _source = source;
        _source.AddHook(HwndHook);
    }

    public bool RegisterHotkey(uint modifiers, uint key)
    {
        if (_isRegistered)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
        }

        _isRegistered = RegisterHotKey(_source.Handle, HOTKEY_ID, modifiers, key);
        return _isRegistered;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isRegistered)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
            _isRegistered = false;
        }

        _source.RemoveHook(HwndHook);
    }
}
