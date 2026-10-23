using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfApp1;

internal sealed record AudioApplication(int Id, string Name, string Label, string State, ImageSource? Icon = null)
{
    public bool Global => Id == 0;
    public string Details => Global ? "全部应用与系统声音 · 不含 Glimpse 自身"
        : (Label == Name ? "" : Name + " · ") + $"{State} · PID {Id}";
    private static readonly ImageSource Globe = CreateGlobe();
    public static AudioApplication GlobalOutput => new(0, "全局声音", "全局识别 · 所有应用声音", "全部输出", Globe);
    private static ImageSource CreateGlobe()
    {
        var geometry = new GeometryGroup();
        geometry.Children.Add(new EllipseGeometry(new Rect(2, 2, 20, 20)));
        geometry.Children.Add(Geometry.Parse("M2,12 H22 M12,2 C4,8 4,16 12,22 M12,2 C20,8 20,16 12,22"));
        var image = new DrawingImage(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(105, 98, 245)), 1.6), geometry));
        image.Freeze(); return image;
    }
}

// Enumerate the same Windows output-session category used by the volume mixer.
// Do this on an MTA thread. Refresh is explicit/on dropdown open; no recording.
internal static class AudioApplications
{
    public static IReadOnlyList<AudioApplication> Enumerate()
    {
        var result = new Dictionary<int, AudioApplication>();
        var parents = ReadParents();
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();
        IDeviceCollection? devices = null;
        int failures = 0, completed = 0;
        try
        {
            Check(enumerator.EnumAudioEndpoints(0, 1, out devices)); // eRender, DEVICE_STATE_ACTIVE
            Check(devices.GetCount(out uint count));
            for (uint index = 0; index < count; index++)
            {
                IDevice? device = null;
                object? managerObject = null;
                ISessionEnumerator? sessions = null;
                try
                {
                    Check(devices.Item(index, out device));
                    Guid iid = typeof(ISessionManager).GUID;
                    Check(device.Activate(ref iid, 23, IntPtr.Zero, out managerObject));
                    var manager = (ISessionManager)managerObject;
                    Check(manager.GetSessionEnumerator(out sessions));
                    Check(sessions.GetCount(out int sessionCount));
                    for (int item = 0; item < sessionCount; item++)
                    {
                        object? controlObject = null;
                        try
                        {
                            Check(sessions.GetSession(item, out controlObject));
                            var control = (ISessionControl)controlObject;
                            Check(control.GetState(out int state));
                            if (state == 2 || control.IsSystemSoundsSession() == 0) continue;
                            Check(control.GetProcessId(out uint processId));
                            if (processId == 0 || processId == Environment.ProcessId) continue;
                            using var process = Process.GetProcessById(FindApplicationRoot((int)processId, parents));
                            if (process.Id == Environment.ProcessId) continue;
                            string name = process.ProcessName;
                            string? executable = null;
                            try
                            {
                                executable = process.MainModule?.FileName;
                                if (executable != null)
                                    name = FileVersionInfo.GetVersionInfo(executable).FileDescription is { Length: > 0 } description ? description : name;
                            }
                            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException) { }
                            string display = ReadDisplayName(control);
                            if (string.IsNullOrWhiteSpace(display) || display.StartsWith('@')) display = name;
                            string activity = state == 1 ? "音频活动" : "暂时空闲";
                            if (!result.TryGetValue(process.Id, out var previous))
                                result[process.Id] = new(process.Id, name, display, activity, ReadIcon(executable));
                            else if (state == 1) result[process.Id] = previous with { State = activity };
                        }
                        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
                        { /* Sessions/processes can disappear during enumeration. */ }
                        finally { Release(controlObject); }
                    }
                    completed++;
                }
                catch (COMException) { failures++; }
                finally { Release(sessions); Release(managerObject); Release(device); }
            }
            if (failures > 0 && completed == 0) throw new InvalidOperationException("无法读取输出设备的音频会话，请确认音频设备可用。");
        }
        finally { Release(devices); Release(enumerator); }
        return result.Values.OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string ReadDisplayName(ISessionControl control)
    {
        Check(control.GetDisplayName(out var value));
        try { return Marshal.PtrToStringUni(value) ?? ""; }
        finally { Marshal.FreeCoTaskMem(value); }
    }
    private static ImageSource? ReadIcon(string? executable)
    {
        if (executable == null) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
            if (icon == null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(24, 24));
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return null; }
    }
    private static int FindApplicationRoot(int pid, Dictionary<int, int> parents)
    {
        // Browser/Electron audio services commonly live in child processes.
        // Merge equal executable names and known WebView helpers, never Explorer.
        int root = pid;
        for (int depth = 0; depth < 16 && parents.TryGetValue(root, out int parent) && parent > 0 && parent != root; depth++)
        {
            try
            {
                using var child = Process.GetProcessById(root);
                using var ancestor = Process.GetProcessById(parent);
                bool helper = child.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
                    || child.ProcessName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)
                    || child.ProcessName.Equals("QtWebEngineProcess", StringComparison.OrdinalIgnoreCase);
                if ((!string.Equals(child.ProcessName, ancestor.ProcessName, StringComparison.OrdinalIgnoreCase) && !helper)
                    || ancestor.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                    || ancestor.StartTime > child.StartTime) break;
                root = parent;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { break; }
        }
        return root;
    }
    private static Dictionary<int, int> ReadParents()
    {
        var result = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
            if (Process32First(snapshot, ref entry))
                do { result[(int)entry.ProcessId] = (int)entry.ParentId; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr Heap;
        public uint Module, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IDeviceCollection devices);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionManager
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out ISessionEnumerator enumerator);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }
}
