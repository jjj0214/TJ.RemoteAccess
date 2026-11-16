// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3SessionLifecycleTests
{
    [Fact]
    public async Task LockRevokesAgentAndUnlockStartsFreshAgentWithoutOverlap()
    {
        var started = new ConcurrentQueue<TaskCompletionSource<bool>>();
        var completed = new ConcurrentQueue<TaskCompletionSource<bool>>();
        int active = 0, maximum = 0, attempts = 0;
        using var life = new M3SessionLifecycle(async token =>
        {
            Interlocked.Increment(ref attempts);
            var entered = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, entered);
            var begin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var end = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            started.Enqueue(begin);
            completed.Enqueue(end);
            begin.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally
            {
                Interlocked.Decrement(ref active);
                end.TrySetResult(true);
            }
        }, _ => Assert.Fail("Agent should not crash."));

        life.StartOrRebind();
        await SpinUntilAsync(() => attempts == 1);
        await SpinUntilAsync(() => started.TryPeek(out _));
        Assert.True(started.TryPeek(out var first));
        await first!.Task.WaitAsync(TimeSpan.FromSeconds(6));
        life.Revoke();
        await SpinUntilAsync(() => Volatile.Read(ref active) == 0);
        life.StartOrRebind();
        await SpinUntilAsync(() => attempts == 2);
        Assert.Equal(1, maximum);
        Assert.Equal(1, active);
        Assert.Equal(3, life.Generation);
        life.Revoke();
        await SpinUntilAsync(() => Volatile.Read(ref active) == 0);
    }

    [Fact]
    public async Task RapidRebindSerializesAndCannotCreateParallelAgents()
    {
        int active = 0, max = 0, calls = 0;
        using var life = new M3SessionLifecycle(async token =>
        {
            Interlocked.Increment(ref calls);
            int entered = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref max, entered);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Decrement(ref active); }
        }, _ => Assert.Fail("Unexpected agent exception."));
        for (int i = 0; i < 30; i++)
        {
            life.StartOrRebind();
            if (i % 2 == 0) life.Revoke();
        }
        life.Revoke();
        await life.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, active);
        Assert.InRange(max, 0, 1);
        Assert.Equal(46, life.Generation); // 30 launches + 16 revocations
    }

    [Fact]
    public async Task LaunchFailureIsReportedAndNotSilentlyRetried()
    {
        int failures = 0, starts = 0;
        using var life = new M3SessionLifecycle(_ =>
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("test fail");
        }, ex =>
        {
            if (ex is InvalidOperationException) Interlocked.Increment(ref failures);
        });
        life.StartOrRebind();
        await life.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, starts);
        Assert.Equal(1, failures);
        Assert.Equal(1, life.Generation);
    }

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, deadline.Token);
        }
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int location, int value)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref location);
                if (value <= previous) return;
            } while (Interlocked.CompareExchange(ref location, value, previous) != previous);
        }
    }
}
