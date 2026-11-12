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

    internal LabSharedFrame? Latest => Volatile.Read(ref _latest);

    internal long Publish(byte[] jpeg)
    {
        if (jpeg is null || jpeg.Length < 4 ||
            jpeg.Length >= LabWire.MaxFrameBytes ||
            jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("Invalid shared JPEG frame.");
        var sequence = Interlocked.Increment(ref _sequence);
        Volatile.Write(ref _latest, new LabSharedFrame(sequence, jpeg));
        return sequence;
    }
}
