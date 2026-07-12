using System.Buffers.Binary;
using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Ipc;

public static class PipeFraming
{
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (payload.Length > ProtocolConstants.MaximumMessageBytes)
        {
            throw new InvalidDataException($"IPC message exceeds {ProtocolConstants.MaximumMessageBytes} bytes.");
        }

        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length <= 0 || length > ProtocolConstants.MaximumMessageBytes)
        {
            throw new InvalidDataException($"Invalid IPC message length {length}.");
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, IpcJson.Options)
            ?? throw new InvalidDataException("IPC message contained JSON null.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("IPC peer closed before a complete message was received.");
            }

            read += count;
        }
    }
}
