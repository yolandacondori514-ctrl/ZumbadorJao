using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZumbidoJao;

internal sealed record AlertRequest(int Version, string Type, Guid RequestId, int BoxNumber);

internal static class AlertProtocol
{
    private const int ProtocolVersion = 1;
    private const string MessageType = "zumbido";
    private const int HeaderLength = sizeof(int);1
    private const int MaxPayloadLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AlertRequest CreateRequest(int boxNumber) =>
        new(ProtocolVersion, MessageType, Guid.NewGuid(), boxNumber);

    public static async Task WriteAsync(
        NetworkStream stream,
        AlertRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (payload.Length is 0 or > MaxPayloadLength)
            throw new InvalidDataException("La trama supera la longitud permitida.");

        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public static async Task<AlertRequest> ReadAsync(
        NetworkStream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);

        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header);
        if (payloadLength is <= 0 or > MaxPayloadLength)
            throw new InvalidDataException("La longitud de la trama no es válida.");

        byte[] payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken);

        AlertRequest? request = JsonSerializer.Deserialize<AlertRequest>(payload, JsonOptions);
        if (request is null)
            throw new InvalidDataException("La trama no contiene una solicitud válida.");

        Validate(request);
        return request;
    }

    private static void Validate(AlertRequest request)
    {
        if (request.Version != ProtocolVersion ||
            !string.Equals(request.Type, MessageType, StringComparison.Ordinal) ||
            request.RequestId == Guid.Empty ||
            request.BoxNumber is < 1 or > 10)
        {
            throw new InvalidDataException("La solicitud de zumbido contiene valores no válidos.");
        }
    }
}
