using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WpfApp1;

internal static class ClipboardSelection
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Called on the WPF dispatcher: clipboard/OLE operations require STA.
    internal static async Task<Native.TextSelection> Read(Native.ScreenPoint point, IntPtr foreground, Func<bool> stillCurrent)
    {
        if (!await Gate.WaitAsync(0)) return new("", null);
        DataObject? snapshot = null;
        uint copiedSequence = 0;
        try
        {
            bool Current() => stillCurrent() && Native.GetForegroundWindow() == foreground;
            var release = Stopwatch.StartNew();
            while (ModifiersDown() && release.ElapsedMilliseconds < 800)
            {
                if (!Current()) return new("", null);
                await Task.Delay(25);
            }
            if (!Current() || ModifiersDown()) return new("", null);

            uint before = GetClipboardSequenceNumber();
            snapshot = Snapshot();
            // Materializing delayed formats can take time: never overwrite a
            // clipboard that changed while taking the backup.
            if (!Current() || GetClipboardSequenceNumber() != before) return new("", null);
            var keys = new[] { Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true) };
            if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length)
            {
                var keyUps = new[] { Key(0x43, true), Key(0x11, true) };
                SendInput((uint)keyUps.Length, keyUps, Marshal.SizeOf<Input>());
                return new("", null);
            }

            var waiting = Stopwatch.StartNew();
            while (waiting.ElapsedMilliseconds < 900)
            {
                await Task.Delay(25);
                if (!Current()) return new("", null);
                uint sequence = GetClipboardSequenceNumber();
                if (sequence == before) continue; // Never read stale clipboard text.
                if (copiedSequence != 0 && sequence != copiedSequence)
                {
                    copiedSequence = 0;
                    return new("", null);
                }
                copiedSequence = sequence;
                try
                {
                    string text = Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : "";
                    if (GetClipboardSequenceNumber() != sequence)
                    {
                        copiedSequence = 0; // Someone else updated it during the read.
                        return new("", null);
                    }
                    return new(text.Length > 16001 ? text[..16001] : text, point);
                }
                catch (ExternalException) { }
            }
            return new("", null);
        }
        catch (Exception ex) when (ex is ExternalException or IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return new("", null);
        }
        finally
        {
            try
            {
                // Ignore later clipboard changes rather than replacing user data.
                if (snapshot != null && copiedSequence != 0 && GetClipboardSequenceNumber() == copiedSequence)
                {
                    if (snapshot.GetFormats(false).Length == 0) Clipboard.Clear();
                    else Clipboard.SetDataObject(snapshot, true);
                }
            }
            catch (ExternalException) { /* A busy clipboard must not crash capture. */ }
            finally { Gate.Release(); }
        }
    }

    private static DataObject Snapshot()
    {
        var snapshot = new DataObject();
        var data = Clipboard.GetDataObject();
        if (data == null) return snapshot;
        foreach (string format in data.GetFormats(false))
        {
            object? value = data.GetData(format, false);
            if (value == null) throw new NotSupportedException("Clipboard format cannot be backed up.");
            object copy;
            if (value is string || value.GetType().IsPrimitive || value is Guid || value is DateTime) copy = value;
            else if (value is string[] strings) copy = strings.Clone();
            else if (value is byte[] bytes) copy = bytes.Clone();
            else if (value is BitmapSource bitmap)
            {
                var clone = bitmap.CloneCurrentValue(); clone.Freeze(); copy = clone;
            }
            else if (value is Stream stream)
            {
                if (!stream.CanSeek || stream.Length > 16 * 1024 * 1024) throw new NotSupportedException("Clipboard stream cannot be backed up.");
                long position = stream.Position;
                var clone = new MemoryStream();
                try { stream.Position = 0; stream.CopyTo(clone); }
                finally { stream.Position = position; }
                clone.Position = 0; copy = clone;
            }
            else throw new NotSupportedException("Clipboard format cannot be safely backed up.");
            snapshot.SetData(format, copy, false);
        }
        return snapshot;
    }

    private static bool ModifiersDown()
    {
        foreach (int key in new[] { 0x11, 0x10, 0x12, 0x5B, 0x5C, 0x01, 0x02 })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    private static Input Key(ushort key, bool up = false) => new()
    {
        Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } }
    };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
