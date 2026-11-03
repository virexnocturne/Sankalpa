using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Blocker.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.Service;

[SupportedOSPlatform("windows")]
public class PipeServer : BackgroundService
{
    public const string DefaultPipeName = "BlockerControl";
    public const int MaxMessageBytes = 64 * 1024; // 64 KB hard cap
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly IBlockStore _store;
    private readonly ILogger<PipeServer>? _logger;
    private readonly string _pipeName;

    public PipeServer(IBlockStore store, ILogger<PipeServer>? logger = null, string pipeName = DefaultPipeName)
    {
        _store = store;
        _logger = logger;
        _pipeName = pipeName;
    }

    public static PipeSecurity CreatePipeSecurity()
    {
        // NOTE: Phase 7 must revisit this when the service runs as SYSTEM.
        var pipeSecurity = new PipeSecurity();

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            systemSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            adminSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            authUsersSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return pipeSecurity;
    }

    public static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        var pipeSecurity = CreatePipeSecurity();

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);
    }

    public static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(ReadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        byte[] buffer = new byte[1024];
        using var ms = new MemoryStream();

        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token);
            if (bytesRead == 0)
            {
                if (ms.Length == 0) return null;
                break;
            }

            for (int i = 0; i < bytesRead; i++)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    string json = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                    if (string.IsNullOrWhiteSpace(json)) return null;
                    return JsonSerializer.Deserialize<Request>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }

                ms.WriteByte(b);
                if (ms.Length > MaxMessageBytes)
                {
                    throw new InvalidOperationException($"Message exceeded hard cap of {MaxMessageBytes} bytes.");
                }
            }
        }

        string remainingJson = Encoding.UTF8.GetString(ms.ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(remainingJson)) return null;
        return JsonSerializer.Deserialize<Request>(remainingJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public static async Task WriteResponseAsync(Stream stream, Response response, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(response) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("Named pipe server listening on '{PipeName}' with concurrent listeners...", _pipeName);

        const int ConcurrentListeners = 4;
        var listenerTasks = new List<Task>();

        for (int i = 0; i < ConcurrentListeners; i++)
        {
            listenerTasks.Add(Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    NamedPipeServerStream? serverStream = null;
                    try
                    {
                        serverStream = CreateServerStream(_pipeName);
                        await serverStream.WaitForConnectionAsync(stoppingToken);

                        var clientStream = serverStream;
                        _ = Task.Run(async () =>
                        {
                            await HandleClientAsync(clientStream, stoppingToken);
                        }, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        serverStream?.Dispose();
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Exception in named pipe listener task; backing off 50ms.");
                        serverStream?.Dispose();
                        try
                        {
                            await Task.Delay(50, stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }, stoppingToken));
        }

        await Task.WhenAll(listenerTasks);
    }

    private async Task HandleClientAsync(NamedPipeServerStream clientStream, CancellationToken stoppingToken)
    {
        try
        {
            using (clientStream)
            {
                Request? request = null;
                try
                {
                    request = await ReadRequestAsync(clientStream, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogWarning("Pipe connection read timed out (exceeded 5s) or was cancelled.");
                    return;
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning("Dropped malformed JSON request from pipe client: {Message}", ex.Message);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    _logger?.LogWarning("Dropped oversized request from pipe client: {Message}", ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Dropped connection due to read error: {Message}", ex.Message);
                    return;
                }

                if (request == null)
                {
                    _logger?.LogWarning("Pipe client disconnected without sending a request.");
                    return;
                }

                Response response;
                try
                {
                    response = CommandHandler.Handle(request, _store);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Unexpected error in CommandHandler.Handle");
                    response = Response.Fail($"Internal server error: {ex.Message}");
                }

                try
                {
                    await WriteResponseAsync(clientStream, response, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Failed to write response to pipe client: {Message}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Unexpected error while handling pipe client: {Message}", ex.Message);
        }
    }
}
