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
            var store = new LabRemoteFileStore(root);
            var first = store.List(Guid.NewGuid(), "");
            Assert.True(first.HasMore);
            Assert.Equal(LabRemoteFileWire.MaxEntries + 1, first.TotalCount);
            Assert.Equal(LabRemoteFileWire.MaxEntries, first.Items.Length);
            var next = store.List(Guid.NewGuid(), "", first.NextOffset);
            Assert.False(next.HasMore);
            Assert.Single(next.Items);
            Assert.Equal(first.TotalCount, first.Items.Length + next.Items.Length);
            Assert.DoesNotContain(next.Items[0].Name, first.Items.Select(item => item.Name));
            Assert.Throws<InvalidDataException>(() => store.List(Guid.NewGuid(), "", first.TotalCount + 1));
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
            store.Mutate(new(Guid.NewGuid(), "CreateFolder", "", "new-folder"));
            Assert.True(Directory.Exists(Path.Combine(root, "new-folder")));
            var first = store.List(Guid.NewGuid(), "");
            Assert.True(first.HasMore);
            Assert.Equal(LabRemoteFileWire.MaxEntries + 1, first.TotalCount);
            Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.EncodeCommand(
                new(Guid.NewGuid(), "Delete", "new-folder", "", 160)));
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
    [Fact]
    public void ServerOwnerSharePolicyIsExplicitScopedAndRevocable()
    {
        string root = Path.Combine(Path.GetTempPath(), "tjra-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new LabServerSettings {
                RemoteFileRoot = root,
                AllowRemoteFileBrowse = true,
                AllowRemoteFileUpload = true,
                AllowRemoteFileDownload = false,
                AllowRemoteFileChanges = false
            };
            var policy = new LabRemoteFileAccessPolicy(settings);
            Assert.Equal(Path.GetFullPath(root), policy.Root);
            Assert.True(policy.Browse);
            Assert.True(policy.Upload);
            Assert.False(policy.Download);
            Assert.False(policy.Changes);
            settings.AllowRemoteFileBrowse = false;
            Assert.False(policy.Browse);
            settings.RemoteFileRoot = Path.Combine(root, "other");
            Assert.False(policy.RootUnchanged);
            Assert.False(policy.Upload);
            Assert.Throws<InvalidDataException>(() =>
                LabRemoteFileAccessPolicy.ValidateRoot(Path.GetPathRoot(root)!));
            Assert.Throws<InvalidDataException>(() =>
                LabRemoteFileAccessPolicy.ValidateRoot("relative\\folder"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void IncompleteStagingIsNeverListedOrAccessibleRemotely()
    {
        string root = Path.Combine(Path.GetTempPath(), "tjra-hidden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string stageName = ".tjra-resume-0123.partial";
            string privatePart = Path.Combine(root, stageName);
            File.WriteAllText(privatePart, "not fully verified");
            File.WriteAllText(Path.Combine(root, "normal.txt"), "public");
            var store = new LabRemoteFileStore(root);
            var entries = store.List(Guid.NewGuid(), "");
            Assert.Single(entries.Items);
            Assert.Equal("normal.txt", entries.Items[0].Name);
            Assert.Throws<InvalidDataException>(() => store.SelectedFile(stageName));
            Assert.Throws<InvalidDataException>(() => store.Mutate(
                new(Guid.NewGuid(), "Delete", stageName)));
            Assert.Throws<InvalidDataException>(() => LabRemoteFileWire.CheckRelative(
                "child/" + stageName));
            Assert.True(File.Exists(privatePart));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
