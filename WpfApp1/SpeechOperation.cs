using System;
using System.Threading;

namespace WpfApp1;

// Reserve the whole operation, including loading and automatic recovery.
internal static class SpeechOperation
{
    private static string? owner;
    public static IDisposable Acquire(string name)
    {
        var previous = Interlocked.CompareExchange(ref owner, name, null);
        if (previous != null) throw new InvalidOperationException($"{previous}正在运行，请先停止后再开始{name}。");
        return new Reservation();
    }
    private sealed class Reservation : IDisposable
    {
        private int released;
        public void Dispose()
        { if (Interlocked.Exchange(ref released, 1) == 0) Interlocked.Exchange(ref owner, null); }
    }
}
