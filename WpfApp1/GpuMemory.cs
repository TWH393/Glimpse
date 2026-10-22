using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1;

internal sealed record GpuMemory(double UsedPercent, double FreeMiB)
{
    public bool UnderPressure => UsedPercent >= 85 || FreeMiB <= 2048;
    public bool HasHeadroom => UsedPercent < 70 && FreeMiB > 3072;

    public static async Task<GpuMemory?> Read()
    {
        // The installed CUDA integration uses the first NVIDIA GPU. Query only
        // its memory counters, not game names, window contents or user activity.
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("nvidia-smi")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in new[] { "--id=0", "--query-gpu=memory.total,memory.free", "--format=csv,noheader,nounits" })
            process.StartInfo.ArgumentList.Add(argument);
        bool started = false;
        try
        {
            started = process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output; await error;
            if (process.ExitCode != 0) return null;
            var values = text.Trim().Split(',');
            if (values.Length != 2
                || !double.TryParse(values[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double total)
                || !double.TryParse(values[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double free)
                || total <= 0 || free < 0 || free > total) return null;
            return new GpuMemory((total - free) / total * 100, free);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException or System.IO.IOException)
        {
            return null; // Unknown telemetry must not cause unload/reload guesses.
        }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
    }
}
