using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1;

// App isolation or explicitly selected global output; never implicitly widens scope.
public static class ApplicationAudioCapture
{
    internal sealed record Packet(double Start, byte[] Pcm);

    internal static async Task Run(int pid, Action<Packet> receive, Func<bool> paused, Action started, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new InvalidOperationException("按应用采音初版需要 Windows 11。");
        bool global = pid == 0;
        using var target = global ? null : Process.GetProcessById(pid);
        var completion = new ActivationHandler();
        // Excluding this app's tree includes other output streams across endpoints.
        // Glimpse/its ASR workers do not render sound; excluding them avoids feedback.
        var activation = new ActivationParameters { Type = 1, ProcessId = (uint)(global ? Environment.ProcessId : pid), Mode = global ? 1 : 0 };
        IntPtr blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>());
        Marshal.StructureToPtr(activation, blob, false);
        var variant = new BlobVariant { Type = 65, Size = (uint)Marshal.SizeOf<ActivationParameters>(), Data = blob };
        var iid = typeof(IAudioClient).GUID;
        IActivateOperation? operation = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        try
        {
            int activated = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref variant, completion, out operation);
            if (activated < 0) completion.Result.TrySetException(Marshal.GetExceptionForHR(activated)!);
            Check(activated);
            // The callback retains its blob until activation completes, even if cancelled.
            client = await completion.Result.Task.WaitAsync(TimeSpan.FromSeconds(15));
            token.ThrowIfCancellationRequested();
            var format = new WaveFormat { Tag = 1, Channels = 1, SampleRate = 16000, BytesPerSecond = 32000, BlockAlign = 2, Bits = 16 };
            Check(client.Initialize(0, 0x80000000u | 0x08000000u | 0x00020000u | 0x00040000u, 0, 0, ref format, IntPtr.Zero));
            using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            Guid captureId = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref captureId, out var service));
            capture = (IAudioCaptureClient)service;
            var clock = Stopwatch.StartNew();
            double epoch = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            Check(client.Start());
            bool stopped = false;
            try
            {
                started();
                while (!token.IsCancellationRequested)
                {
                    if (target?.HasExited == true) throw new InvalidOperationException("音频来源应用已退出，会话已停止。");
                    if (paused())
                    {
                        if (!stopped) { Check(client.Stop()); Check(client.Reset()); stopped = true; }
                        await Task.Delay(40, token);
                        continue;
                    }
                    if (stopped) { Check(client.Start()); stopped = false; }
                    ready.WaitOne(40);
                    Check(capture.GetNextPacketSize(out uint available));
                    while (available > 0)
                    {
                        Check(capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out ulong qpc));
                        try
                        {
                            var bytes = new byte[checked((int)frames * 2)];
                            if ((flags & 2) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
                            double start = (flags & 4) == 0 && qpc > 0
                                ? qpc / 10000000.0 - epoch : clock.Elapsed.TotalSeconds - frames / 16000.0;
                            receive(new Packet(Math.Max(0, start), bytes));
                        }
                        finally { Check(capture.ReleaseBuffer(frames)); }
                        Check(capture.GetNextPacketSize(out available));
                    }
                }
            }
            finally { client.Stop(); }
        }
        finally
        {
            // A pending activation may still read the PROPVARIANT blob.
            if (completion.Result.Task.IsCompleted)
            {
                Marshal.FreeHGlobal(blob);
                if (client == null && completion.Result.Task.IsCompletedSuccessfully)
                    Marshal.ReleaseComObject(completion.Result.Task.Result);
            }
            else _ = completion.Result.Task.ContinueWith(task =>
            {
                Marshal.FreeHGlobal(blob);
                if (task.IsCompletedSuccessfully) Marshal.ReleaseComObject(task.Result);
            }, TaskScheduler.Default);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (operation != null) Marshal.ReleaseComObject(operation);
            GC.KeepAlive(completion);
        }
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    [StructLayout(LayoutKind.Sequential)] private struct ActivationParameters { public int Type; public uint ProcessId; public int Mode; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct BlobVariant
    { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public uint Size; [FieldOffset(16)] public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] internal struct WaveFormat
    { public ushort Tag, Channels; public uint SampleRate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, ref BlobVariant parameters,
        IActivationHandler completion, out IActivateOperation operation);

    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivationHandler { [PreserveSig] int ActivateCompleted(IActivateOperation operation); }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateOperation { [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object client); }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationHandler : IActivationHandler, IAgileObject
    {
        internal readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IActivateOperation operation)
        {
            try { Check(operation.GetActivateResult(out int hr, out var client)); Check(hr); Result.TrySetResult((IAudioClient)client); }
            catch (Exception ex) { Result.TrySetException(ex); }
            return 0;
        }
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, ref WaveFormat format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
