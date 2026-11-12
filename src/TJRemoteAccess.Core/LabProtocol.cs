// SPDX-License-Identifier: GPL-2.0-or-later
// TJRA Lab framed binary protocol: only over mutually authenticated TLS.
using System.Buffers.Binary;
using System.IO;

namespace TJ.RemoteAccess.Lab;

internal enum LabMessage : byte
{
    Hello = 1, Welcome = 2, JpegFrame = 3, Key = 4,
    Pointer = 5, Status = 6, KeepAlive = 7
}
internal enum LabRole : byte { ViewOnly = 0, Controller = 1 }
internal readonly record struct LabPacket(LabMessage Type, byte[] Payload);
internal readonly record struct LabWelcome(LabRole Role, int Width, int Height,
                                             int OriginX, int OriginY);

internal static class LabWire
{
    internal const int MaxFrameBytes = 6 * 1024 * 1024;
    internal const int MaxControlPacketBytes = 32;
    internal const byte Version = 1;

    internal static async Task WriteAsync(Stream stream, SemaphoreSlim writeMutex,
                                           LabMessage type, ReadOnlyMemory<byte> body,
                                           CancellationToken stop)
    {
        int size = checked(body.Length + 1);
        if (size > MaxFrameBytes) throw new InvalidDataException("Outgoing packet too large.");
        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), size);
        header[4] = (byte)type;
        await writeMutex.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            // Serialized under one lock; no duplicate multi-megabyte JPEG.
            await stream.WriteAsync(header, stop).ConfigureAwait(false);
            if (!body.IsEmpty)
                await stream.WriteAsync(body, stop).ConfigureAwait(false);
            await stream.FlushAsync(stop).ConfigureAwait(false);
        }
        finally { writeMutex.Release(); }
    }

    internal static async Task<LabPacket> ReadAsync(Stream stream, CancellationToken stop,
                                                   int maxPacketBytes = MaxFrameBytes)
    {
        if (maxPacketBytes is < 1 or > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPacketBytes));
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, stop).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length < 1 || length > maxPacketBytes)
            throw new InvalidDataException("Incoming packet size is invalid.");
        // Read the tag separately so a JPEG payload is allocated only once.
        var tag = new byte[1];
        await stream.ReadExactlyAsync(tag, stop).ConfigureAwait(false);
        var payload = new byte[length - 1];
        await stream.ReadExactlyAsync(payload, stop).ConfigureAwait(false);
        return new LabPacket((LabMessage)tag[0], payload);
    }

    internal static byte[] Welcome(LabWelcome value)
    {
        var result = new byte[17];
        result[0] = (byte)value.Role;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(1, 4), value.Width);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(5, 4), value.Height);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(9, 4), value.OriginX);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(13, 4), value.OriginY);
        return result;
    }

    internal static LabWelcome ParseWelcome(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 17 || bytes[0] > (byte)LabRole.Controller)
            throw new InvalidDataException("Invalid server welcome.");
        int w = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(1, 4));
        int h = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(5, 4));
        if (w < 1 || w > 16000 || h < 1 || h > 16000)
            throw new InvalidDataException("Invalid remote desktop dimensions.");
        return new LabWelcome((LabRole)bytes[0], w, h,
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(9, 4)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(13, 4)));
    }

    internal static byte[] Key(ushort virtualKey, bool pressed)
    {
        var bytes = new byte[3];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), virtualKey);
        bytes[2] = pressed ? (byte)1 : (byte)0;
        return bytes;
    }

    internal static (ushort Key, bool Pressed) ParseKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 3 || bytes[2] > 1)
            throw new InvalidDataException("Invalid keyboard input.");
        ushort key = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(0, 2));
        if (key == 0 || key > 254)
            throw new InvalidDataException("Invalid virtual-key code.");
        return (key, bytes[2] == 1);
    }

    // Cursor position is normalized to 0..65535 across the entire virtual
    // Windows desktop; the server maps it to its own screen coordinates.
    internal static byte[] Pointer(ushort normalizedX, ushort normalizedY, byte buttons)
    {
        if ((buttons & ~7) != 0) throw new InvalidDataException("Invalid mouse buttons.");
        var bytes = new byte[5];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), normalizedX);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), normalizedY);
        bytes[4] = buttons;
        return bytes;
    }
    internal static (ushort X, ushort Y, byte Buttons) ParsePointer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 5 || (bytes[4] & ~7) != 0)
            throw new InvalidDataException("Invalid pointer message.");
        return (
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(0, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2, 2)), bytes[4]);
    }
}
