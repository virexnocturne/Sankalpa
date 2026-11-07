using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Blocker.Core;

public class PipeResult
{
    public bool Connected { get; set; }
    public Response? Response { get; set; }
    public string? Error { get; set; }

    public static PipeResult NotConnected(string error = "Service is not running") =>
        new() { Connected = false, Error = error };

    public static PipeResult Success(Response response) =>
        new() { Connected = true, Response = response };

    public static PipeResult ReadError(string error) =>
        new() { Connected = true, Error = error, Response = Response.Fail(error) };
}

public interface IPipeClient
{
    Task<PipeResult> SendRequestAsync(Request request, CancellationToken cancellationToken = default);
}

public class PipeClient : IPipeClient
{
    public const string DefaultPipeName = "BlockerControl";
    public const int DefaultTimeoutMs = 2000;

    private readonly string _pipeName;
    private readonly int _timeoutMs;

    public PipeClient(string pipeName = DefaultPipeName, int timeoutMs = DefaultTimeoutMs)
    {
        _pipeName = pipeName;
        _timeoutMs = timeoutMs;
    }

    public async Task<PipeResult> SendRequestAsync(Request request, CancellationToken cancellationToken = default)
    {
        using var pipeStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeoutCts = new CancellationTokenSource(_timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await pipeStream.ConnectAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PipeResult.NotConnected("Connection timed out. Service is not running.");
        }
        catch (Exception ex)
        {
            return PipeResult.NotConnected($"Could not connect to service: {ex.Message}");
        }

        try
        {
            int ioTimeoutMs = Math.Max(_timeoutMs, 10000);
            using var ioTimeoutCts = new CancellationTokenSource(ioTimeoutMs);
            using var linkedIoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ioTimeoutCts.Token);

            string json = JsonSerializer.Serialize(request) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await pipeStream.WriteAsync(bytes, 0, bytes.Length, linkedIoCts.Token).ConfigureAwait(false);
            await pipeStream.FlushAsync(linkedIoCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipeStream, Encoding.UTF8, leaveOpen: true);
            string? line = await reader.ReadLineAsync(linkedIoCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return PipeResult.ReadError("Received empty response from service.");
            }

            var response = JsonSerializer.Deserialize<Response>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (response == null)
            {
                return PipeResult.ReadError("Failed to deserialize response from service.");
            }

            return PipeResult.Success(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PipeResult.ReadError("Operation timed out waiting for service response.");
        }
        catch (Exception ex)
        {
            return PipeResult.ReadError($"Error communicating with service: {ex.Message}");
        }
    }
}
