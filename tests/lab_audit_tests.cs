// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabAuditWriterTests
{
    [Fact]
    public async Task WritesSanitizedStructuredEventsInUserOwnedFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tjra-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var audit = new LabAuditWriter(folder, maxBytes: 8192, backups: 2);
            Assert.False(Directory.Exists(folder));
            Assert.True(audit.TryRecord("Connected", "Viewer connected\r\nadmin\t"));
            Assert.True(audit.TryRecord("Denied", "Unsupported protocol"));
            await audit.DisposeAsync();
            Assert.Equal(0, audit.WriteErrors);
            var lines = File.ReadAllLines(audit.LogFile);
            Assert.Equal(2, lines.Length);
            var first = JsonDocument.Parse(lines[0]).RootElement;
            Assert.Equal("Connected", first.GetProperty("Type").GetString());
            Assert.Equal("Viewer connected  admin ", first.GetProperty("Detail").GetString());
            Assert.NotNull(first.GetProperty("TimestampUtc").GetString());
            Assert.False(audit.TryRecord("Later", "closed"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task RotationIsBoundedAndOldEventsAreNotRetainedIndefinitely()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tjra-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var audit = new LabAuditWriter(folder, maxBytes: 800, backups: 2);
            for (int i = 0; i < 700; i++)
                audit.TryRecord("Cycle", "Lifecycle event " + i);
            await audit.DisposeAsync();
            Assert.True(File.Exists(audit.LogFile));
            var files = Directory.GetFiles(folder, "access.jsonl*");
            Assert.InRange(files.Length, 1, 3);
            foreach (var file in files)
                Assert.InRange(new FileInfo(file).Length, 1, 800);
            Assert.True(audit.Dropped >= 0);
            Assert.Equal(0, audit.WriteErrors);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task RejectArbitraryEventNames()
    {
        var audit = new LabAuditWriter();
        try
        {
            Assert.Throws<ArgumentException>(() => audit.TryRecord("Unsafe\nType", "xx"));
            Assert.Throws<ArgumentException>(() => audit.TryRecord("", "xx"));
        }
        finally { await audit.DisposeAsync(); }
    }
}
