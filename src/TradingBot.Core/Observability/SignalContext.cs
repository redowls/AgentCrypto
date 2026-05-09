namespace TradingBot.Core.Observability;

public static class SignalContext
{
    private static readonly AsyncLocal<Guid?> _current = new();

    public static Guid? Current => _current.Value;

    public static IDisposable BeginSignal(Guid signalId)
    {
        var prev = _current.Value;
        _current.Value = signalId;
        return new Scope(prev);
    }

    /// <summary>
    /// Pushes a correlation scope from a numeric (database-assigned) signal id.
    /// The id is encoded into the low 8 bytes of a deterministic Guid so that
    /// the same SignalId always yields the same correlation Guid.
    /// </summary>
    public static IDisposable BeginSignal(long signalId)
        => BeginSignal(ToGuid(signalId));

    internal static Guid ToGuid(long signalId)
    {
        // Deterministic: high 8 bytes zero, low 8 bytes = signalId little-endian.
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[8..], signalId);
        return new Guid(bytes);
    }

    private sealed class Scope(Guid? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
