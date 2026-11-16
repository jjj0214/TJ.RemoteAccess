// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3ServiceStatusTests
{
    [Fact]
    public void DiagnosticIsAtomicBoundedAndNeverGrantsRemoteAccess()
    {
        string folder = Path.Combine(Path.GetTempPath(), "TJRA-M3-StatusTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var status = new M3ServiceStatus(folder);
            status.TryWrite("Starting");
            status.TryWrite("AgentAuthenticated", agentPid: 4321, sessionId: 2);
            using (var json = JsonDocument.Parse(File.ReadAllText(status.StatusPath)))
            {
                var root = json.RootElement;
                Assert.Equal("AgentAuthenticated", root.GetProperty("State").GetString());
                Assert.Equal(4321, root.GetProperty("AgentPid").GetInt32());
                Assert.Equal(2, root.GetProperty("SessionId").GetInt32());
                Assert.True(root.GetProperty("StatusOnly").GetBoolean());
                Assert.False(root.GetProperty("RemoteAccessEnabled").GetBoolean());
                Assert.False(root.ToString().Contains("password", StringComparison.OrdinalIgnoreCase));
            }
            status.TryWrite("SessionRevoked");
            using (var json = JsonDocument.Parse(File.ReadAllText(status.StatusPath)))
            {
                Assert.Equal("SessionRevoked", json.RootElement.GetProperty("State").GetString());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("AgentPid").ValueKind);
            }
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RejectArbitraryDiagnosticContentsAndFakeAuthenticatedState()
    {
        var status = new M3ServiceStatus(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Throws<ArgumentException>(() => status.TryWrite("ControlGranted"));
        Assert.Throws<ArgumentException>(() => status.TryWrite("AgentAuthenticated"));
        Assert.Throws<ArgumentException>(() => status.TryWrite("AgentFailed", diagnostic: "secret path\\file"));
        Assert.Throws<ArgumentException>(() => status.TryWrite("Stopped", agentPid: 123));
    }
}
