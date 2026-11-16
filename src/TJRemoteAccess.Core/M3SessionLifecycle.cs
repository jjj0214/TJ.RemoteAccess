// SPDX-License-Identifier: GPL-2.0-or-later
// Single-agent, fail-closed service supervisor. Session changes revoke the
// previous agent before starting another. Never carries desktop or input data.
namespace TJ.RemoteAccess.M3;

internal sealed class M3SessionLifecycle : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task> _runAgent;
    private readonly Action<Exception> _reportFailure;
    private CancellationTokenSource? _active;
    private Task _tail = Task.CompletedTask;
    private bool _stopped;
    private long _generation;

    internal M3SessionLifecycle(Func<CancellationToken, Task> runAgent,
                                Action<Exception> reportFailure)
    {
        _runAgent = runAgent ?? throw new ArgumentNullException(nameof(runAgent));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    internal long Generation
    {
        get { lock (_sync) return _generation; }
    }

    internal Task Completion
    {
        get { lock (_sync) return _tail; }
    }

    internal void StartOrRebind() => Replace(startAgent: true);
    internal void Revoke() => Replace(startAgent: false);

    private void Replace(bool startAgent)
    {
        lock (_sync)
        {
            if (_stopped) return;
            _active?.Cancel();
            CancellationTokenSource? old = _active;
            CancellationTokenSource? next = startAgent ? new CancellationTokenSource() : null;
            Task previous = _tail;
            _active = next;
            _generation++;
            _tail = Task.Run(async () =>
            {
                // No two agent runs may overlap, including during fast
                // lock/unlock/logon transitions. The launched child is killed
                // by the previous run's finally/Dispose before proceeding.
                try { await previous.ConfigureAwait(false); }
                finally { old?.Dispose(); }
                if (next is null || next.IsCancellationRequested) return;
                try
                {
                    await _runAgent(next.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (next.IsCancellationRequested) { }
                catch (Exception failure)
                {
                    // Never log tokens, names, keys, command lines or paths.
                    try { _reportFailure(failure); }
                    catch { /* diagnostic failures must not crash SCM */ }
                }
            });
        }
    }

    public void Dispose()
    {
        Task done;
        CancellationTokenSource? last;
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            last = _active;
            last?.Cancel();
            _active = null;
            done = _tail;
        }
        // SCM stop must be bounded. Even on timeout the process boundary
        // prevents this service from authorizing a remote session.
        try { done.Wait(TimeSpan.FromSeconds(8)); }
        catch (AggregateException) { }
        if (done.IsCompleted) last?.Dispose();
    }
}
