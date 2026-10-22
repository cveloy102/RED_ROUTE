using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using RedRoute.Core;

namespace RedRoute.Platform;

public static class PipeProtocol
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
        if (bytes.Length > Protocol.MaximumMessageBytes) throw new IOException("Сообщение слишком большое.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 1 || size > Protocol.MaximumMessageBytes) throw new IOException("Некорректная длина сообщения.");
        var data = new byte[size]; await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data, Protocol.Json) ?? throw new IOException("Пустое сообщение.");
    }
    public static async Task<ServiceResponse> CallAsync(ServiceRequest request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        PipeServerIdentity.Verify(pipe);
        await WriteAsync(pipe, request, timeout.Token);
        return await ReadAsync<ServiceResponse>(pipe, timeout.Token);
    }
}
