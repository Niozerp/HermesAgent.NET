namespace HermesAgent.Cli;

/// <summary>
/// Single-line console spinner shown while awaiting LLM responses.
/// Refreshes in place with carriage returns so it composes cleanly with
/// raw Console.Write streaming used for response deltas.
/// </summary>
public sealed class ResponseSpinner : IDisposable
{
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private const int IntervalMs = 80;
    private const string Dim = "\x1b[2m";
    private const string Reset = "\x1b[0m";

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile string _label = "thinking";
    private int _lastWidth;
    private bool _disposed;

    public bool IsRunning
    {
        get { lock (_gate) return _loop is not null && !_loop.IsCompleted; }
    }

    /// <summary>Starts spinning with the given label (no-op if already spinning; updates label).</summary>
    public void Start(string label = "thinking")
    {
        lock (_gate)
        {
            if (_disposed) return;
            _label = label;
            if (_loop is not null && !_loop.IsCompleted) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => SpinAsync(token));
        }
    }

    /// <summary>Updates the label while spinning (e.g. "running tool X").</summary>
    public void SetLabel(string label) => _label = label;

    /// <summary>Stops the spinner and erases its line. Idempotent.</summary>
    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            if (_loop is null || _loop.IsCompleted) { Erase(); return; }
            _cts?.Cancel();
            loop = _loop;
        }
        try { loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* cancelled */ }
        Erase();
        lock (_gate)
        {
            _loop = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task SpinAsync(CancellationToken ct)
    {
        var i = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = Frames[i % Frames.Length];
                var plain = $"{frame} {_label}…";
                // Pad to previous width so a shorter label fully overwrites a longer one.
                var pad = new string(' ', Math.Max(0, _lastWidth - plain.Length));
                Console.Write('\r' + Dim + plain + Reset + pad);
                _lastWidth = plain.Length;
                i++;
                await Task.Delay(IntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { /* normal stop */ }
    }

    private void Erase()
    {
        if (_lastWidth <= 0) return;
        Console.Write("\r" + new string(' ', _lastWidth + 2) + "\r");
        _lastWidth = 0;
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) _disposed = true;
    }
}
