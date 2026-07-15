using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace PCHelper.GameBarWidget;

/// <summary>
/// The widget has one deliberately narrow IPC path: a read-only request to the
/// signed-in RigPilot user agent. It has no service-pipe or hardware access.
/// </summary>
internal sealed class GameBarUserAgentBridge
{
    private const string UserAgentPipeName = "pchelper.useragent.v2";
    private const int ProtocolVersion = 2;
    private const int MaximumMessageBytes = 2 * 1024 * 1024;

    public async Task<GameBarBridgeSnapshot> GetOverlayStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using NamedPipeClientStream pipe = new(
                ".",
                UserAgentPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            JsonObject request = new()
            {
                ["protocolVersion"] = JsonValue.CreateNumberValue(ProtocolVersion),
                ["requestId"] = JsonValue.CreateStringValue(Guid.NewGuid().ToString("N")),
                ["command"] = JsonValue.CreateStringValue("GetOverlayStatus"),
                ["expectedStateRevision"] = null,
                ["idempotencyKey"] = null,
                ["payload"] = null,
            };
            await WriteFrameAsync(pipe, request.Stringify(), timeout.Token).ConfigureAwait(false);
            string responseText = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            JsonObject response = JsonObject.Parse(responseText);
            bool success = response.TryGetValue("success", out IJsonValue? successValue)
                && successValue.ValueType == JsonValueType.Boolean
                && successValue.GetBoolean();
            if (!success)
            {
                string error = response.TryGetValue("errorMessage", out IJsonValue? errorValue)
                    && errorValue.ValueType == JsonValueType.String
                    ? errorValue.GetString()
                    : "The RigPilot user agent rejected the request.";
                return new GameBarBridgeSnapshot(false, error, null, null);
            }

            JsonObject? payload = response.TryGetValue("payload", out IJsonValue? payloadValue)
                && payloadValue.ValueType == JsonValueType.Object
                ? payloadValue.GetObject()
                : null;
            if (payload is null)
            {
                return new GameBarBridgeSnapshot(false, "The user agent returned no overlay state.", null, null);
            }

            string rtss = GetString(payload, "rtss", "message");
            string gameBar = GetString(payload, "gameBarMessage");
            string capture = GetString(payload, "captureMessage");
            return new GameBarBridgeSnapshot(true, gameBar, rtss, capture);
        }
        catch (Exception)
        {
            return new GameBarBridgeSnapshot(
                false,
                "The read-only bridge is unavailable. Install the signed widget through RigPilot, then restart the tray agent so it can grant this exact package SID.",
                null,
                null);
        }
    }

    private static string GetString(JsonObject source, string property, string? childProperty = null)
    {
        if (!source.TryGetValue(property, out IJsonValue? value) || value.ValueType == JsonValueType.Null)
        {
            return string.Empty;
        }
        if (childProperty is not null && value.ValueType == JsonValueType.Object)
        {
            return GetString(value.GetObject(), childProperty);
        }
        return value.ValueType == JsonValueType.String ? value.GetString() : string.Empty;
    }

    private static async Task WriteFrameAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = BitConverter.GetBytes(body.Length);
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header, 0);
        if (length is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("The user-agent response frame has an invalid length.");
        }
        byte[] body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The user agent closed the bridge before completing a frame.");
            }
            offset += read;
        }
    }
}

internal sealed class GameBarBridgeSnapshot
{
    public GameBarBridgeSnapshot(bool isConnected, string summary, string? rtss, string? capture)
    {
        IsConnected = isConnected;
        Summary = summary;
        Rtss = rtss;
        Capture = capture;
    }

    public bool IsConnected { get; }

    public string Summary { get; }

    public string? Rtss { get; }

    public string? Capture { get; }
}
