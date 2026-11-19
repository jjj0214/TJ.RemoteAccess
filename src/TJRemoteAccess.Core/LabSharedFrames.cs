// SPDX-License-Identifier: GPL-2.0-or-later
// Single-producer latest-frame fanout for the interactive Lab Server.
// Each published byte array is owned by this store and never modified again.
// Viewers hold immutable snapshots; a slow Viewer drops old frames instead of
// building an unbounded queue. Each TLS session still encrypts independently.
namespace TJ.RemoteAccess.Lab;

internal sealed record LabSharedFrame(long Sequence, byte[] Jpeg);

internal sealed class LabSharedFrameStore
{
    private LabSharedFrame? _latest;
    private long _sequence;
    private int _quality = 65;
    private int _framesPerSecond = 6;
    internal int Quality => Volatile.Read(ref _quality);
    internal int FramesPerSecond => Volatile.Read(ref _framesPerSecond);
    internal void Configure(byte quality, byte framesPerSecond)
    {
        LabWire.CaptureSettings(quality, framesPerSecond);
        Interlocked.Exchange(ref _quality, quality);
        Interlocked.Exchange(ref _framesPerSecond, framesPerSecond);
    }

    internal LabSharedFrame? Latest => Volatile.Read(ref _latest);

    internal long Publish(byte[] jpeg)
    {
        if (jpeg is null || jpeg.Length < 4 ||
            jpeg.Length >= LabWire.MaxFrameBytes ||
            jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("Invalid shared JPEG frame.");
        // One capture producer: identical JPEG bytes imply identical rendered
        // pixels, so don't make three Viewers re-download and decode them.
        // Manual Refresh still explicitly resends Latest when requested.
        var prior = Volatile.Read(ref _latest);
        if (prior is not null && jpeg.AsSpan().SequenceEqual(prior.Jpeg))
            return prior.Sequence;
        var sequence = Interlocked.Increment(ref _sequence);
        Volatile.Write(ref _latest, new LabSharedFrame(sequence, jpeg));
        return sequence;
    }
}
