// SPDX-License-Identifier: GPL-2.0-or-later
// M3 Windows Service <-> interactive session IPC frame contract.
// FAIL CLOSED: no remote-desktop, clipboard, command or input message type is
// accepted by this control-plane-only milestone.
using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace TJ.RemoteAccess.M3;

internal enum M3MessageType : byte
{
    Challenge = 1,
    Response = 2,
    Ready = 3,
    Heartbeat = 4,
    Goodbye = 5,
    AgentStatus = 6
}

internal readonly record struct M3Packet(M3MessageType Type, ReadOnlyMemory<byte> Payload);

internal static class M3Wire
{
    internal const byte ProtocolVersion = 1;
    internal const int MaxPacketPayload = 1024;
    internal const int KeyBytes = 32;
    internal const int NonceBytes = 32;
    private const int HeaderLength = 10;
    private static readonly byte[] Magic = { (byte)'T', (byte)'J', (byte)'R', (byte)'A' };

    internal static async Task WriteAsync(Stream stream, M3MessageType type,
                                          ReadOnlyMemory<byte> payload,
                                          CancellationToken cancel = default)
    {
        ValidateTypeAndLength(type, payload.Length);
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        header[4] = ProtocolVersion;
        header[5] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(6), payload.Length);
        await stream.WriteAsync(header, cancel).ConfigureAwait(false);
        if (payload.Length != 0)
            await stream.WriteAsync(payload, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    internal static async Task<M3Packet> ReadAsync(Stream stream,
                                                    CancellationToken cancel = default)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancel).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) ||
            header[4] != ProtocolVersion)
            throw new InvalidDataException("Unknown M3 IPC magic or protocol version.");
        var type = (M3MessageType)header[5];
        int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(6));
        ValidateTypeAndLength(type, len); // BEFORE allocation
        var payload = new byte[len];
        if (len != 0)
            await stream.ReadExactlyAsync(payload, cancel).ConfigureAwait(false);
        return new(type, payload);
    }

    private static void ValidateTypeAndLength(M3MessageType type, int length)
    {
        if (length < 0 || length > MaxPacketPayload)
            throw new InvalidDataException("Oversized IPC control message.");
        bool allowed = type switch {
            M3MessageType.Challenge => length == NonceBytes,
            M3MessageType.Response => length == 32,
            M3MessageType.Ready => length == 0,
            M3MessageType.Heartbeat => length == 0,
            M3MessageType.Goodbye => length == 0,
            M3MessageType.AgentStatus => length is >= 1 and <= 128,
            _ => false
        };
        if (!allowed)
            throw new InvalidDataException("Unsupported message type or invalid payload.");
    }
}

// Attestation keys are NOT CLI args or serialized to config. A later
// privileged launcher must deliver this key to an exact child process via
// a private inherited handle. Until then: status-only test harness.
internal static class M3AgentProof
{
    internal static byte[] Calculate(ReadOnlySpan<byte> key,
                                     ReadOnlySpan<byte> freshChallenge,
                                     uint expectedPid, uint expectedSessionId)
    {
        if (key.Length != M3Wire.KeyBytes || freshChallenge.Length != M3Wire.NonceBytes ||
            expectedPid == 0 || expectedSessionId == uint.MaxValue)
            throw new ArgumentException("Invalid M3 agent attestation material.");
        Span<byte> transcript = stackalloc byte[8 + M3Wire.NonceBytes];
        BinaryPrimitives.WriteUInt32BigEndian(transcript[0..4], expectedPid);
        BinaryPrimitives.WriteUInt32BigEndian(transcript[4..8], expectedSessionId);
        freshChallenge.CopyTo(transcript[8..]);
        return HMACSHA256.HashData(key, transcript);
    }

    internal static bool Verify(ReadOnlySpan<byte> key,
                                ReadOnlySpan<byte> challenge,
                                uint pinnedPid, uint pinnedSessionId,
                                ReadOnlySpan<byte> proof)
    {
        if (proof.Length != 32) return false;
        byte[] expected = Calculate(key, challenge, pinnedPid, pinnedSessionId);
        try { return CryptographicOperations.FixedTimeEquals(expected, proof); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    // Caller MUST have already checked the remote named-pipe kernel PID and
    // Windows session ID, and compared with its own launched child.
    internal static async Task AuthenticateParentAsync(Stream pipe, byte[] ephemeralKey,
                    uint kernelPid, uint kernelSessionId, uint launchedPid,
                    uint launchedSessionId, CancellationToken cancel)
    {
        if (ephemeralKey is null || ephemeralKey.Length != M3Wire.KeyBytes)
            throw new AuthenticationException("M3 session key not securely provisioned.");
        if (kernelPid == 0 || kernelPid != launchedPid ||
            kernelSessionId != launchedSessionId)
            throw new AuthenticationException("Agent PID or Windows logon session mismatch.");
        var challenge = RandomNumberGenerator.GetBytes(M3Wire.NonceBytes);
        try
        {
            await M3Wire.WriteAsync(pipe, M3MessageType.Challenge, challenge, cancel);
            var answer = await M3Wire.ReadAsync(pipe, cancel);
            if (answer.Type != M3MessageType.Response ||
                !Verify(ephemeralKey, challenge, launchedPid, launchedSessionId, answer.Payload.Span))
                throw new AuthenticationException("Invalid M3 agent proof.");
            await M3Wire.WriteAsync(pipe, M3MessageType.Ready, ReadOnlyMemory<byte>.Empty, cancel);
        }
        finally { CryptographicOperations.ZeroMemory(challenge); }
    }

    internal static async Task AuthenticateChildAsync(Stream pipe, byte[] ephemeralKey,
                                    uint ownPid, uint sessionId, CancellationToken cancel)
    {
        if (ephemeralKey is null || ephemeralKey.Length != M3Wire.KeyBytes ||
            ownPid == 0 || sessionId == uint.MaxValue)
            throw new AuthenticationException("M3 agent launch identity or key invalid.");
        var message = await M3Wire.ReadAsync(pipe, cancel);
        if (message.Type != M3MessageType.Challenge)
            throw new AuthenticationException("M3 service challenge missing.");
        var proof = Calculate(ephemeralKey, message.Payload.Span, ownPid, sessionId);
        try
        {
            await M3Wire.WriteAsync(pipe, M3MessageType.Response, proof, cancel);
        }
        finally { CryptographicOperations.ZeroMemory(proof); }
        var ready = await M3Wire.ReadAsync(pipe, cancel);
        if (ready.Type != M3MessageType.Ready)
            throw new AuthenticationException("M3 service did not authorize agent.");
    }
}
