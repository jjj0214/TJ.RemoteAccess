// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabSharedFrameStoreTests
{
    private static byte[] Jpeg(byte value) =>
        new byte[] { 0xFF, 0xD8, value, 0xFF, 0xD9 };

    [Fact]
    public void NewFrameStoreHasNoFramesAndPublishesMonotonically()
    {
        var store = new LabSharedFrameStore();
        Assert.Null(store.Latest);
        Assert.Equal(1L, store.Publish(Jpeg(1)));
        Assert.Equal(1L, store.Latest!.Sequence);
        Assert.Equal(2L, store.Publish(Jpeg(2)));
        Assert.Equal(2L, store.Latest!.Sequence);
        Assert.Equal((byte)2, store.Latest.Jpeg[2]);
    }

    [Fact]
    public void OldViewerSnapshotRemainsValidWhenNewImageIsPublished()
    {
        var store = new LabSharedFrameStore();
        var firstBytes = Jpeg(1);
        store.Publish(firstBytes);
        var first = store.Latest!;
        var nextBytes = Jpeg(2);
        store.Publish(nextBytes);
        var second = store.Latest!;
        Assert.Same(firstBytes, first.Jpeg);
        Assert.Same(nextBytes, second.Jpeg);
        Assert.NotSame(first, second);
        Assert.Equal((byte)1, first.Jpeg[2]);
        Assert.Equal((byte)2, second.Jpeg[2]);
        Assert.True(second.Sequence > first.Sequence);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { })]
    public void RejectsInvalidOrTruncatedJpeg(byte[] bytes)
    {
        var store = new LabSharedFrameStore();
        Assert.Throws<InvalidDataException>(() => store.Publish(bytes));
        Assert.Null(store.Latest);
    }

    [Fact]
    public void RejectsExcessiveFrameBeforeMakingItVisible()
    {
        var store = new LabSharedFrameStore();
        var large = new byte[LabWire.MaxFrameBytes];
        large[0] = 0xFF;
        large[1] = 0xD8;
        Assert.Throws<InvalidDataException>(() => store.Publish(large));
        Assert.Null(store.Latest);
    }

    [Fact]
    public async Task ThreeReadersShareFrameReferenceWithoutCopyingAndSkipOldVersions()
    {
        var store = new LabSharedFrameStore();
        var input = Jpeg(5);
        store.Publish(input);
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() => {
            var first = store.Latest!;
            Assert.Same(input, first.Jpeg);
            return first;
        }));
        var received = await Task.WhenAll(readers);
        Assert.All(received, r => Assert.Same(received[0], r));
        for (int i = 0; i < 100; ++i)
            store.Publish(Jpeg((byte)i));
        var last = store.Latest!;
        Assert.True(last.Sequence > received[0].Sequence);
        Assert.Equal((byte)99, last.Jpeg[2]);
        Assert.Equal((byte)5, received[0].Jpeg[2]);
    }

    [Fact]
    public async Task ConcurrentReadersAlwaysSeeCompleteFrameWithMatchingSequence()
    {
        var store = new LabSharedFrameStore();
        store.Publish(Jpeg(0));
        var reading = Enumerable.Range(0, 3).Select(_ => Task.Run(() => {
            long previous = 0;
            for (int j = 0; j < 500; ++j)
            {
                var snapshot = store.Latest!;
                Assert.Equal((byte)0xFF, snapshot.Jpeg[0]);
                Assert.Equal((byte)0xD8, snapshot.Jpeg[1]);
                Assert.Equal((byte)0xD9, snapshot.Jpeg[^1]);
                Assert.True(snapshot.Sequence >= previous);
                previous = snapshot.Sequence;
            }
        }));
        var writing = Task.Run(() => {
            for (int j = 1; j <= 500; ++j) store.Publish(Jpeg((byte)j));
        });
        await Task.WhenAll(reading.Append(writing));
        Assert.Equal(501L, store.Latest!.Sequence);
    }
}
