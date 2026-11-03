using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Blocker.Core;

/// <summary>
/// Handles Chrome Native Messaging stream framing:
/// 4-byte little-endian length prefix followed by UTF-8 encoded JSON.
/// Capped at 1 MB per message.
/// </summary>
public static class NativeMessageFraming
{
    public const int MaxMessageSize = 1024 * 1024; // 1 MB

    /// <summary>
    /// Reads one framed message from the stream.
    /// Returns null on clean EOF (0 bytes read when reading header).
    /// Throws EndOfStreamException on partial/truncated message.
    /// Throws InvalidDataException if message length exceeds MaxMessageSize or is negative.
    /// </summary>
    public static string? ReadMessage(Stream stream, int maxSizeBytes = MaxMessageSize)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] lengthBuffer = new byte[4];
        int headerBytesRead = ReadExactOrEof(stream, lengthBuffer, 0, 4);
        if (headerBytesRead == 0)
        {
            return null; // Clean EOF
        }
        if (headerBytesRead < 4)
        {
            throw new EndOfStreamException($"Stream ended prematurely while reading message length ({headerBytesRead}/4 bytes read).");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length < 0)
        {
            throw new InvalidDataException($"Negative message length: {length}");
        }
        if (length > maxSizeBytes)
        {
            throw new InvalidDataException($"Message length {length} exceeds maximum allowed size of {maxSizeBytes} bytes.");
        }
        if (length == 0)
        {
            return string.Empty;
        }

        byte[] payloadBuffer = new byte[length];
        int payloadBytesRead = ReadExactOrEof(stream, payloadBuffer, 0, length);
        if (payloadBytesRead < length)
        {
            throw new EndOfStreamException($"Stream ended prematurely while reading message payload ({payloadBytesRead}/{length} bytes read).");
        }

        return Encoding.UTF8.GetString(payloadBuffer);
    }

    /// <summary>
    /// Writes a framed message to the stream with a 4-byte little-endian length prefix.
    /// </summary>
    public static void WriteMessage(Stream stream, string messageJson, int maxSizeBytes = MaxMessageSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(messageJson);

        byte[] payloadBytes = Encoding.UTF8.GetBytes(messageJson);
        if (payloadBytes.Length > maxSizeBytes)
        {
            throw new InvalidDataException($"Message payload length {payloadBytes.Length} exceeds maximum allowed size of {maxSizeBytes} bytes.");
        }

        byte[] lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payloadBytes.Length);

        stream.Write(lengthBuffer, 0, 4);
        if (payloadBytes.Length > 0)
        {
            stream.Write(payloadBytes, 0, payloadBytes.Length);
        }
        stream.Flush();
    }

    private static int ReadExactOrEof(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }
}
