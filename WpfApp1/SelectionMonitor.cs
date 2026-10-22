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
    private long downTimestamp, selectionGestureAt;
    private bool pressed, disposed;
    private bool ownInteraction;
    private int revision;
    private IntPtr foreground;
    private Task<Native.TextSelection>? reading;
    public bool Enabled { get; set; } = true;
    public bool UseClipboardFallback { get; set; } = true;
    public event Action<string, Native.ScreenPoint, long>? SelectionReady;
    public Func<Native.ScreenPoint, bool>? IsPopupPoint { get; set; }
    public event Action<IntPtr, long, bool>? PointerPressed;

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
                    downTimestamp = Stopwatch.GetTimestamp();
                    long pointerDownAt = downTimestamp;
                    var point = item.Point;
                    // Hit-test now, before activation, layout or queued WPF input.
                    bool insidePopup = IsPopupPoint?.Invoke(point) == true;
                    // Snapshot the target before WPF processes the click: expanding
                    // the popup can move its rectangle before this queued callback.
                    var hit = Native.WindowFromPoint(point);
                    var target = Native.GetAncestor(hit, 2); // GA_ROOT
                    if (target == IntPtr.Zero) target = hit;
                    Native.GetWindowThreadProcessId(target, out var targetProcess);
                    ownInteraction = insidePopup || targetProcess == (uint)Environment.ProcessId;
                    // Input must reach the popup before any outside-click dismissal.
                    // Normal priority runs ahead of WPF Input and can hide the
                    // launcher before its mouse-down handler gets the click.
                    dispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(() => { if (!disposed) { debounce.Stop(); PointerPressed?.Invoke(target, pointerDownAt, insidePopup); } }));
                }
                else if (message.ToInt32() == 0x0202 && pressed)
                {
                    pressed = false;
                    if (ownInteraction)
                    {
                        previousTime = 0;
                        return CallNextHookEx(hook, code, message, data);
                    }
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
                            long gestureAt = downTimestamp;
                            dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (disposed || !Enabled) return;
                                anchor = point; foreground = target; selectionGestureAt = gestureAt; debounce.Stop(); debounce.Start();
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
        bool automationBusy = reading is { IsCompleted: false };
        if (automationBusy && !UseClipboardFallback) return; // Bound stalled UIA calls to one.
        int current = revision; var target = foreground; var point = anchor;
        long gestureAt = selectionGestureAt;
        if (!automationBusy) reading = Task.Run(() => Native.ReadSelectionAt(point, target));
        try
        {
            Native.TextSelection selection = new("", null);
            if (!automationBusy)
            {
                try { selection = await reading!.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { /* Copy fallback can still work if UIA stalls. */ }
            }
            bool Current() => !disposed && Enabled && current == revision && !pressed && Native.GetForegroundWindow() == target;
            if (Current() && UseClipboardFallback && selection.AllowCopy && string.IsNullOrWhiteSpace(selection.Text))
                selection = await ClipboardSelection.Read(point, target, Current);
            if (!disposed && Enabled && current == revision && Native.GetForegroundWindow() == target && !string.IsNullOrWhiteSpace(selection.Text))
                SelectionReady?.Invoke(selection.Text, selection.Anchor ?? point, gestureAt);
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
