using System;
using System.IO;
using System.Text;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class NativeMessageFramingTests
{
    private class ChunkedStream : MemoryStream
    {
        private readonly int _maxChunkSize;

        public ChunkedStream(byte[] data, int maxChunkSize = 2) : base(data)
        {
            _maxChunkSize = maxChunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int toRead = Math.Min(count, _maxChunkSize);
            return base.Read(buffer, offset, toRead);
        }
    }

    [Fact]
    public void RoundTrip_SingleMessage_Succeeds()
    {
        using var ms = new MemoryStream();
        string original = "{\"type\":\"heartbeat\",\"version\":\"1.0.0\"}";

        NativeMessageFraming.WriteMessage(ms, original);
        ms.Position = 0;

        string? result = NativeMessageFraming.ReadMessage(ms);
        Assert.Equal(original, result);
    }

    [Fact]
    public void RoundTrip_MultipleMessagesBackToBack_ReadsInOrder()
    {
        using var ms = new MemoryStream();
        string msg1 = "{\"id\":1,\"content\":\"first\"}";
        string msg2 = "{\"id\":2,\"content\":\"second\"}";
        string msg3 = "{\"id\":3,\"content\":\"third\"}";

        NativeMessageFraming.WriteMessage(ms, msg1);
        NativeMessageFraming.WriteMessage(ms, msg2);
        NativeMessageFraming.WriteMessage(ms, msg3);

        ms.Position = 0;

        Assert.Equal(msg1, NativeMessageFraming.ReadMessage(ms));
        Assert.Equal(msg2, NativeMessageFraming.ReadMessage(ms));
        Assert.Equal(msg3, NativeMessageFraming.ReadMessage(ms));
        Assert.Null(NativeMessageFraming.ReadMessage(ms)); // EOF
    }

    [Fact]
    public void ReadMessage_ChunkedStream_HandlesPartialReadsCorrectly()
    {
        using var source = new MemoryStream();
        string original = "{\"key\":\"A longer JSON payload that will be split across multiple 2-byte chunk reads.\"}";
        NativeMessageFraming.WriteMessage(source, original);

        using var chunked = new ChunkedStream(source.ToArray(), maxChunkSize: 3);

        string? read = NativeMessageFraming.ReadMessage(chunked);
        Assert.Equal(original, read);
    }

    [Fact]
    public void ReadMessage_CleanEof_ReturnsNull()
    {
        using var ms = new MemoryStream();
        Assert.Null(NativeMessageFraming.ReadMessage(ms));
    }

    [Fact]
    public void ReadMessage_TruncatedHeader_ThrowsEndOfStreamException()
    {
        using var ms = new MemoryStream(new byte[] { 0x05, 0x00 }); // only 2 bytes instead of 4
        Assert.Throws<EndOfStreamException>(() => NativeMessageFraming.ReadMessage(ms));
    }

    [Fact]
    public void ReadMessage_TruncatedPayload_ThrowsEndOfStreamException()
    {
        using var ms = new MemoryStream();
        // 4 bytes length saying 10 bytes payload
        ms.Write(BitConverter.GetBytes(10));
        ms.Write(Encoding.UTF8.GetBytes("1234")); // only 4 bytes payload
        ms.Position = 0;

        Assert.Throws<EndOfStreamException>(() => NativeMessageFraming.ReadMessage(ms));
    }

    [Fact]
    public void ReadMessage_OversizedLength_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        int oversized = NativeMessageFraming.MaxMessageSize + 1;
        ms.Write(BitConverter.GetBytes(oversized));
        ms.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(() => NativeMessageFraming.ReadMessage(ms));
        Assert.Contains("exceeds maximum allowed size", ex.Message);
    }

    [Fact]
    public void WriteMessage_OversizedMessage_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        string huge = new string('x', NativeMessageFraming.MaxMessageSize + 1);

        Assert.Throws<InvalidDataException>(() => NativeMessageFraming.WriteMessage(ms, huge));
    }

    [Fact]
    public void RoundTrip_EmptyMessage_Succeeds()
    {
        using var ms = new MemoryStream();
        NativeMessageFraming.WriteMessage(ms, string.Empty);
        ms.Position = 0;

        string? result = NativeMessageFraming.ReadMessage(ms);
        Assert.Equal(string.Empty, result);
    }
}
