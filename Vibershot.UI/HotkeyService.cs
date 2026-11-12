using System.Windows.Interop;

namespace Vibershot.UI;

/// <summary>
/// Registers a Windows global hotkey bound to an existing WPF window and raises
/// an event when it is pressed. Registration survives app focus (OS-wide).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Window _owner;
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private readonly int _id;
    private bool _disposed;

    public string Description { get; }

    public event EventHandler<int>? Triggered;

    public HotkeyService(Window owner, int id, uint modifiers, uint virtualKey, string description)
    {
        _owner = owner;
        _id = id;
        Description = description;

        var handle = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("Could not attach to the window message hook.");

        _hook = (IntPtr ptr, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == NativeMethods.WM_HOTKEY && (int)wParam == id)
            {
                // Raise on the UI thread.
                Application.Current.Dispatcher.Invoke(() => Triggered?.Invoke(this, id));
                handled = true;
            }

            return IntPtr.Zero;
        };

        _source.AddHook(_hook);

        if (!NativeMethods.RegisterHotKey(handle, id, modifiers, virtualKey))
        {
            _source.RemoveHook(_hook);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var handle = new WindowInteropHelper(_owner).Handle;
        NativeMethods.UnregisterHotKey(handle, _id);
        _source.RemoveHook(_hook);
        _disposed = true;
    }
}
