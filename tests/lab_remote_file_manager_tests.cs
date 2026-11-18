// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class RemoteFileManagerTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("sub/../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/Windows")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("CON")]
    [InlineData("a/.")]
    [InlineData("a/..")]
    [InlineData("a/COM1")]
    [InlineData("a/b/c/d/e/f/g/h/i")]
    public void RemoteManagerBlocksHostAndTraversalPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.CheckRelative(path));
    }

    [Fact]
    public void ManagerCommandsAndResponsesAreStrictlyBounded()
    {
        Guid id = Guid.NewGuid();
        var command = new LabRemoteFileCommand(id, "List", "incoming");
        Assert.Equal(command, LabRemoteFileWire.DecodeCommand(
            LabRemoteFileWire.EncodeCommand(command)));
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.EncodeCommand(
            new(Guid.NewGuid(), "Delete", "")));
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.DecodeCommand(
            System.Text.Encoding.UTF8.GetBytes("{broken json")));
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.EncodeCommand(
            new(Guid.NewGuid(), "Move", "something")));
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.EncodeReply(
            new(id, true, "", new LabRemoteFileItem[161])));
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.DecodeReply(
            new byte[70_000]));
    }

    [Fact]
    public void ApprovedRootSupportsFolderListingRenameAndSingleItemRemoval()
    {
        string root = Path.Combine(Path.GetTempPath(), "tjra-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LabRemoteFileStore(root);
            File.WriteAllText(Path.Combine(root, "first.txt"), "test");
            var listing = store.List(Guid.NewGuid(), "");
            Assert.Single(listing.Items);
            Assert.Equal("first.txt", listing.Items[0].Name);
            Assert.False(listing.Items[0].IsDirectory);
            Assert.EndsWith("first.txt", store.SelectedFile("first.txt"));
            Assert.Throws<InvalidDataException>(() => store.SelectedFile("../first.txt"));

            store.Mutate(new(Guid.NewGuid(), "CreateFolder", "", "new"));
            Assert.Contains(store.List(Guid.NewGuid(), "").Items, i =>
                i.Name == "new" && i.IsDirectory);
            File.WriteAllText(Path.Combine(root, "new", "safe.txt"), "inside");
            store.Mutate(new(Guid.NewGuid(), "Rename", "new/safe.txt", "renamed.txt"));
            Assert.True(File.Exists(Path.Combine(root, "new", "renamed.txt")));
            Assert.Throws<IOException>(() => store.Mutate(
                new(Guid.NewGuid(), "Delete", "new")));
            store.Mutate(new(Guid.NewGuid(), "Delete", "new/renamed.txt"));
            store.Mutate(new(Guid.NewGuid(), "Delete", "new"));
            Assert.Throws<InvalidDataException>(() => store.Mutate(
                new(Guid.NewGuid(), "Delete", "")));
            Assert.True(File.Exists(Path.Combine(root, "first.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RemoteSelectedDownloadIsScopedAndOldRequestStillParses()
    {
        Guid id = Guid.NewGuid();
        byte[] packet = LabRemoteFileWire.DownloadRequest(id, "new/hello.txt");
        Assert.Equal((id, "new/hello.txt"), LabRemoteFileWire.ParseDownloadRequest(packet));
        Assert.Equal((id, ""), LabRemoteFileWire.ParseDownloadRequest(id.ToByteArray()));
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.DownloadRequest(id, "../foo"));
        Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.ParseDownloadRequest(new byte[3]));
    }
    [Fact]
    public void TooManyRemoteEntriesMustNotSilentlyTruncateFolderDownloads()
    {
        string root = Path.Combine(Path.GetTempPath(), "tjra-many-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i <= LabRemoteFileWire.MaxEntries; i++)
                File.WriteAllText(Path.Combine(root, "file-" + i.ToString("D3") + ".txt"), "x");
            Assert.Throws<InvalidDataException>(() =>
                new LabRemoteFileStore(root).List(Guid.NewGuid(), ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RemoteUploadDestinationRoundTripsAndLegacyOfferStillWorks()
    {
        var legacy = new LabFileOffer(Guid.NewGuid(), "test.txt", 3, new byte[32]);
        Assert.Equal("", LabFileTransfer.ParseOffer(LabFileTransfer.OfferBytes(legacy)).TargetFolder);
        var nested = legacy with { TargetFolder = "some/approved" };
        var decoded = LabFileTransfer.ParseOffer(LabFileTransfer.OfferBytes(nested));
        Assert.Equal(nested.TargetFolder, decoded.TargetFolder);
        Assert.Throws<InvalidDataException>(() =>
            LabFileTransfer.OfferBytes(legacy with { TargetFolder = "../../escape" }));
        Assert.Throws<InvalidDataException>(() => LabFileTransfer.ParseOffer(
            LabFileTransfer.OfferBytes(nested)[..^1]));
    }
    [Fact]
    public void FullRemoteFolderRejectsCreateBeforeAnyFilesystemMutation()
    {
        string root = Path.Combine(Path.GetTempPath(), "tjra-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i < LabRemoteFileWire.MaxEntries; i++)
                File.WriteAllText(Path.Combine(root, "entry-" + i.ToString("D3") + ".txt"), "x");
            var store = new LabRemoteFileStore(root);
            Assert.Equal(LabRemoteFileWire.MaxEntries, store.List(Guid.NewGuid(), "").Items.Length);
            Assert.Throws<InvalidDataException>(() => store.Mutate(new(
                Guid.NewGuid(), "CreateFolder", "", "not-created")));
            Assert.False(Directory.Exists(Path.Combine(root, "not-created")));
            Assert.Equal(LabRemoteFileWire.MaxEntries, Directory.GetFileSystemEntries(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void RemoteCopyRespectsApprovedRootAndNeverOverwrites()
    {
        string root = Path.Combine(Path.GetTempPath(), "tjra-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LabRemoteFileStore(root);
            File.WriteAllText(Path.Combine(root, "source.txt"), "original");
            Directory.CreateDirectory(Path.Combine(root, "child"));
            store.Mutate(new(Guid.NewGuid(), "Copy", "source.txt", ""));
            Assert.Equal("original", File.ReadAllText(
                Path.Combine(root, "source (copy 1).txt")));
            store.Mutate(new(Guid.NewGuid(), "Copy", "source.txt", "child"));
            Assert.Equal("original", File.ReadAllText(
                Path.Combine(root, "child", "source.txt")));
            Assert.Throws<InvalidDataException>(() => store.Mutate(
                new(Guid.NewGuid(), "Copy", "child", "")));
            Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.EncodeCommand(
                new(Guid.NewGuid(), "Copy", "source.txt", "../outside")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "outside", "source.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
