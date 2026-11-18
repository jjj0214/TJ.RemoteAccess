// SPDX-License-Identifier: GPL-2.0-or-later
// Bounded, manually approved clipboard text transport over the existing TLS channel.
// Clipboard content is never logged or automatically synchronized.
using System.Text;

namespace TJ.RemoteAccess.Lab;

internal static class LabClipboardWire
{
    internal const int MaxTextBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Request(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Missing clipboard request ID.");
        return id.ToByteArray();
    }

    internal static Guid ParseRequest(ReadOnlySpan<byte> body)
    {
        if (body.Length != 16) throw new InvalidDataException("Invalid clipboard request.");
        var id = new Guid(body);
        if (id == Guid.Empty) throw new InvalidDataException("Missing clipboard request ID.");
        return id;
    }

    internal static byte[] Text(Guid id, string text)
    {
        if (id == Guid.Empty || text is null) throw new InvalidDataException("Invalid clipboard text.");
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Clipboard text includes forbidden controls.");
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid clipboard Unicode.", ex); }
        if (bytes.Length is < 1 or > MaxTextBytes)
            throw new InvalidDataException("Clipboard text exceeds 16 KiB or is empty.");
        byte[] body = new byte[16 + bytes.Length];
        id.TryWriteBytes(body.AsSpan(0, 16));
        bytes.CopyTo(body.AsSpan(16));
        return body;
    }

    internal static (Guid Id, string Text) ParseText(ReadOnlySpan<byte> body)
    {
        if (body.Length < 17 || body.Length > 16 + MaxTextBytes)
            throw new InvalidDataException("Invalid clipboard text length.");
        Guid id = ParseRequest(body[..16]);
        string text;
        try { text = StrictUtf8.GetString(body[16..]); }
        catch (DecoderFallbackException ex)
        { throw new InvalidDataException("Invalid clipboard UTF-8.", ex); }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Clipboard text includes forbidden controls.");
        return (id, text);
    }

    internal static byte[] Decision(Guid id, bool approved)
    {
        byte[] result = new byte[17];
        Request(id).CopyTo(result, 0);
        result[16] = approved ? (byte)1 : (byte)0;
        return result;
    }

    internal static (Guid Id, bool Approved) ParseDecision(ReadOnlySpan<byte> body)
    {
        if (body.Length != 17 || body[16] > 1)
            throw new InvalidDataException("Invalid clipboard decision.");
        return (ParseRequest(body[..16]), body[16] == 1);
    }
}
