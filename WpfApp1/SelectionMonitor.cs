using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WpfApp1;

/// <summary>Observes selection gestures without altering the clipboard or focus.</summary>
internal sealed class SelectionMonitor : IDisposable
{
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    private readonly MouseHook callback;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer debounce;
    private IntPtr hook;
    private Native.ScreenPoint down, previousUp, anchor;
    private uint previousTime;
    private bool pressed, disposed;
    private int revision;
    private IntPtr foreground;
    private Task<string>? reading;
    public bool Enabled { get; set; } = true;
    public event Action<string, Native.ScreenPoint>? SelectionReady;
    public event Action<Native.ScreenPoint>? PointerPressed;

    public SelectionMonitor(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        debounce.Tick += async (_, _) => { debounce.Stop(); await ReadSelection(); };
        callback = Observe;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法启用自动划词；仍可使用取词快捷键。");
    }

    private IntPtr Observe(int code, IntPtr message, IntPtr data)
    {
        // This callback must return immediately; UI Automation runs off this hook.
        if (code >= 0 && !disposed)
        {
            var item = Marshal.PtrToStructure<MouseData>(data);
            if ((item.Flags & 1) == 0)
            {
                if (message.ToInt32() == 0x0201)
                {
                    down = item.Point; pressed = true; revision++;
                    var point = item.Point;
                    dispatcher.BeginInvoke(new Action(() => { if (!disposed) { debounce.Stop(); PointerPressed?.Invoke(point); } }));
                }
                else if (message.ToInt32() == 0x0202 && pressed)
                {
                    pressed = false;
                    bool dragged = Distance(down, item.Point) > 25;
                    bool doubleClick = unchecked(item.Time - previousTime) <= GetDoubleClickTime() && Distance(previousUp, item.Point) < 36;
                    previousUp = item.Point; previousTime = item.Time;
                    if (Enabled && (dragged || doubleClick))
                    {
                        var target = Native.GetForegroundWindow();
                        Native.GetWindowThreadProcessId(target, out var processId);
                        if (target != IntPtr.Zero && processId != (uint)Environment.ProcessId)
                        {
                            var point = item.Point;
                            dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (disposed || !Enabled) return;
                                anchor = point; foreground = target; debounce.Stop(); debounce.Start();
                            }));
                        }
                    }
                }
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private async Task ReadSelection()
    {
        if (!Enabled || disposed || pressed || Native.GetForegroundWindow() != foreground) return;
        if (reading is { IsCompleted: false }) return; // Bound stalled UIA calls to one.
        int current = revision; var target = foreground; var point = anchor;
        reading = Task.Run(Native.SelectedText);
        try
        {
            var text = await reading.WaitAsync(TimeSpan.FromSeconds(2));
            if (!disposed && Enabled && current == revision && Native.GetForegroundWindow() == target && !string.IsNullOrWhiteSpace(text))
                SelectionReady?.Invoke(text, point);
        }
        catch (Exception) { /* Unsupported/slow applications remain silent. */ }
    }

    private static long Distance(Native.ScreenPoint a, Native.ScreenPoint b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
    public void Dispose()
    {
        disposed = true; revision++; debounce.Stop();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public Native.ScreenPoint Point; public uint MouseDataValue, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
