namespace TradingBot.Observability.Logging;

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
