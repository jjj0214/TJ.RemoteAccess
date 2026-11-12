// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3IpcSecurityTests
{
    [Fact]
    public async Task KnownControlMessagesCanRoundTripAcrossStrictFraming()
    {
        using var data = new MemoryStream();
        await M3Wire.WriteAsync(data, M3MessageType.AgentStatus, new byte[] { 1, 2, 3 });
        data.Position = 0;
        var read = await M3Wire.ReadAsync(data);
        Assert.Equal(M3MessageType.AgentStatus, read.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.Payload.ToArray());
    }

    [Fact]
    public async Task UnknownMessagesAndRemoteInputCannotReachAgentViaM3()
    {
        using var data = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            M3Wire.WriteAsync(data, (M3MessageType)99, new byte[] { 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            M3Wire.WriteAsync(data, M3MessageType.AgentStatus, new byte[1025]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            M3Wire.WriteAsync(data, M3MessageType.Heartbeat, new byte[] { 1 }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 2)]
    [InlineData(5, 255)]
    public async Task InvalidFrameVersionMagicOrTypeIsRejected(byte first, byte version)
    {
        using var data = new MemoryStream(new byte[] {
            first, 0, 0, 0, version, 1, 0, 0, 0, 32
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => M3Wire.ReadAsync(data));
    }

    [Fact]
    public async Task RejectOversizedFrameBeforeAllocatingAnyPayload()
    {
        using var data = new MemoryStream(new byte[] {
            (byte)'T', (byte)'J', (byte)'R', (byte)'A', 1, 6,
            0, 0, 8, 0
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => M3Wire.ReadAsync(data));
    }

    [Fact]
    public void AttestationProofIsBoundToChallengeKernelPidAndSessionId()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] proof = M3AgentProof.Calculate(key, challenge, 4567, 3);
        Assert.True(M3AgentProof.Verify(key, challenge, 4567, 3, proof));
        Assert.False(M3AgentProof.Verify(key, challenge, 4568, 3, proof));
        Assert.False(M3AgentProof.Verify(key, challenge, 4567, 4, proof));
        Assert.False(M3AgentProof.Verify(key, RandomNumberGenerator.GetBytes(32), 4567, 3, proof));
        Assert.False(M3AgentProof.Verify(RandomNumberGenerator.GetBytes(32), challenge, 4567, 3, proof));
        Assert.False(M3AgentProof.Verify(key, challenge, 4567, 3, new byte[6]));
        Assert.Throws<ArgumentException>(() => M3AgentProof.Calculate(new byte[8], challenge, 4567, 3));
        CryptographicOperations.ZeroMemory(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("anything\\malicious")]
    [InlineData("unsafe/pipe")]
    [InlineData(" ")]
    public void PipeNamesCannotEscapeThePrivateTJRANameSpace(string name)
    {
        Assert.Throws<ArgumentException>(() => M3WindowsPipe.PipeName(name));
    }

    [Fact]
    public async Task InvalidSessionKeyFailsBeforeAnyHandshakeBytesAreSent()
    {
        using var pipe = new MemoryStream();
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            M3AgentProof.AuthenticateParentAsync(pipe, new byte[16],
                567, 2, 567, 2, CancellationToken.None));
        Assert.Equal(0, pipe.Length);
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            M3AgentProof.AuthenticateChildAsync(pipe, new byte[16],
                567, 2, CancellationToken.None));
        Assert.Equal(0, pipe.Length);
    }

    [Fact]
    public void WindowsPipeRefusesAccountAndSystemSidBeforeOpeningAHandle()
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = WindowsIdentity.GetCurrent().User!;
        Assert.Throws<AuthenticationException>(() =>
            M3WindowsPipe.CreateRestrictedServer(Guid.NewGuid().ToString("N"), account));
        Assert.Throws<AuthenticationException>(() =>
            M3WindowsPipe.CreateRestrictedServer(Guid.NewGuid().ToString("N"),
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
    }

    [Fact]
    public async Task Win32PipeExplicitDaclLocalOnlyAndMutualAttestationAreOperational()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = M3WindowsPipe.GetTokenLogonSid(identity.AccessToken);
        Assert.NotEqual(identity.User, sid);
        string name = Guid.NewGuid().ToString("N");
        using var server = M3WindowsPipe.CreateRestrictedServer(name, sid);
        using var client = M3WindowsPipe.CreateLocalClient(name);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Task waiting = server.WaitForConnectionAsync(stop.Token);
        await client.ConnectAsync(5000, stop.Token);
        await waiting;
        uint pid = (uint)Environment.ProcessId;
        uint session = (uint)Process.GetCurrentProcess().SessionId;
        var peer = M3WindowsPipe.GetVerifiedKernelPeer(server, pid, session);
        Assert.Equal(pid, peer.Pid);
        Assert.Equal(session, peer.SessionId);
        Assert.Throws<AuthenticationException>(() =>
            M3WindowsPipe.GetVerifiedKernelPeer(server, pid + 1, session));
        Assert.Throws<AuthenticationException>(() =>
            M3WindowsPipe.GetVerifiedKernelPeer(server, pid, session + 1));
        byte[] sharedKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var service = M3AgentProof.AuthenticateParentAsync(
                server, sharedKey, peer.Pid, peer.SessionId, pid, session, stop.Token);
            var agent = M3AgentProof.AuthenticateChildAsync(
                client, sharedKey, pid, session, stop.Token);
            await Task.WhenAll(service, agent);
            await M3Wire.WriteAsync(client, M3MessageType.Heartbeat,
                ReadOnlyMemory<byte>.Empty, stop.Token);
            var heart = await M3Wire.ReadAsync(server, stop.Token);
            Assert.Equal(M3MessageType.Heartbeat, heart.Type);
        }
        finally { CryptographicOperations.ZeroMemory(sharedKey); }
    }

    [Fact]
    public async Task AgentMismatchFailsBeforeAnyHandshake()
    {
        using var transport = new MemoryStream();
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            M3AgentProof.AuthenticateParentAsync(transport, new byte[32],
                kernelPid: 12, kernelSessionId: 1, launchedPid: 13,
                launchedSessionId: 1, CancellationToken.None));
        Assert.Equal(0, transport.Length);
    }
}
