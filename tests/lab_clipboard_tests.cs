// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabClipboardTransportTests
{
    [Fact]
    public void OneTimeClipboardTextHasBoundedUtf8AndRequestId()
    {
        Guid id = Guid.NewGuid();
        string text = "Korean 한국어\nMultiline\tAllowed";
        byte[] encoded = LabClipboardWire.Text(id, text);
        var decoded = LabClipboardWire.ParseText(encoded);
        Assert.Equal(id, decoded.Id);
        Assert.Equal(text, decoded.Text);
        Assert.Equal(id, LabClipboardWire.ParseRequest(LabClipboardWire.Request(id)));
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.Request(Guid.Empty));
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.ParseRequest(new byte[4]));
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.Text(id, ""));
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.Text(id, "secret\0control"));
        Assert.Throws<InvalidDataException>(() =>
            LabClipboardWire.Text(id, "invalid\uD800surrogate"));
        Assert.Throws<InvalidDataException>(() =>
            LabClipboardWire.Text(id, new string('x', LabClipboardWire.MaxTextBytes + 1)));
        Assert.Equal(LabClipboardWire.MaxTextBytes + 16,
            LabClipboardWire.Text(id, new string('x', LabClipboardWire.MaxTextBytes)).Length);
    }

    [Fact]
    public void ClipboardProtocolRejectsForgeryAndInvalidResponses()
    {
        Guid id = Guid.NewGuid();
        byte[] answer = LabClipboardWire.Decision(id, true);
        Assert.Equal((id, true), LabClipboardWire.ParseDecision(answer));
        answer[16] = 2;
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.ParseDecision(answer));
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.ParseDecision(new byte[0]));
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.ParseText(new byte[17]));

        var invalidUtf8 = new byte[17];
        id.TryWriteBytes(invalidUtf8);
        invalidUtf8[16] = 0xff;
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.ParseText(invalidUtf8));
        Assert.Throws<InvalidDataException>(() => LabClipboardWire.ParseText(new byte[17_000]));
    }

    [Fact]
    public async Task FramedClipboardTextAndDecisionRoundTrip()
    {
        Guid id = Guid.NewGuid();
        using var data = new MemoryStream();
        using var mutex = new SemaphoreSlim(1, 1);
        await LabWire.WriteAsync(data, mutex, LabMessage.ClipboardSet,
            LabClipboardWire.Text(id, "manual only"), CancellationToken.None);
        await LabWire.WriteAsync(data, mutex, LabMessage.ClipboardDecision,
            LabClipboardWire.Decision(id, false), CancellationToken.None);
        data.Position = 0;
        var packet = await LabWire.ReadAsync(data, CancellationToken.None);
        Assert.Equal(LabMessage.ClipboardSet, packet.Type);
        Assert.Equal("manual only", LabClipboardWire.ParseText(packet.Payload).Text);
        packet = await LabWire.ReadAsync(data, CancellationToken.None);
        Assert.Equal(LabMessage.ClipboardDecision, packet.Type);
        Assert.Equal((id, false), LabClipboardWire.ParseDecision(packet.Payload));
    }
}
