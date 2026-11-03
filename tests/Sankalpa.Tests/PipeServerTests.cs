using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Blocker.Core.Tests;

[SupportedOSPlatform("windows")]
public class PipeServerTests
{
    private class InProcessTestStore : IBlockStore
    {
        public Profile? GetProfileByName(string name) => null;
        public Profile? GetProfileById(int id) => null;
        public List<Profile> ListProfiles() => new();
        public Profile SaveProfile(Profile profile) => profile;
        public bool RenameProfile(int id, string newName) => true;
        public bool DeleteProfileByName(string name) => true;
        public bool DeleteProfile(int id) => true;
        public void DisableAllProfiles() { }
        public BlockSession? GetActiveSession() => null;
        public BlockSession? GetSessionById(int id) => null;
        public BlockSession StartSession(int profileId, long durationSeconds, bool locked) => new();
        public void EndActiveSession() { }
        public void EndSession(int sessionId) { }
        public bool IsEmergencyUnlockPresent() => false;
        public void ConsumeEmergencyUnlock() { }
        public bool CleanHosts() => true;
        public bool ServiceElevated { get; set; } = true;
        public bool HostsBlockingOk { get; set; } = true;
        public HostsState HostsState { get; set; } = HostsState.Idle;
        public int HostsDomainCount { get; set; } = 0;
        public string? HostsLastError { get; set; }
        public List<string> GetCurrentHostsDomains() => new();
        public void RecordBlockEvent(BlockEvent evt) { }
        public List<BlockEvent> GetBlockEvents(long sinceId) => new();
        public FallbackPlanner FallbackPlanner { get; } = new();
        public FallbackPlanResult? LastFallbackPlan { get; set; }
        public DnsConfig GetDnsConfig() => new();
        public void SaveDnsConfig(DnsConfig config) { }
    }

    [Fact]
    public async Task PipeServer_Ping_Garbage_Oversized_AndPingRecovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipeName = $"BlockerTestPipe_{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();
        var store = new InProcessTestStore();
        var server = new PipeServer(store, pipeName: pipeName);

        var serverTask = server.StartAsync(cts.Token);
        // Allow server loop to start listening
        await Task.Delay(200);

        try
        {
            // 1. Initial Ping works
            using (var client1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client1.ConnectAsync(3000);
                byte[] pingBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Request { Command = CommandNames.Ping }) + "\n");
                await client1.WriteAsync(pingBytes, 0, pingBytes.Length);
                await client1.FlushAsync();

                using var reader = new StreamReader(client1, Encoding.UTF8);
                string? line = await reader.ReadLineAsync();
                Assert.NotNull(line);
                var resp = JsonSerializer.Deserialize<Response>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(resp);
                Assert.True(resp.Ok);
                Assert.Equal("pong", resp.Data);
            }

            // 2. Garbage message gets rejected / dropped
            using (var client2 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client2.ConnectAsync(3000);
                byte[] garbageBytes = Encoding.UTF8.GetBytes("THIS IS MALFORMED GARBAGE THAT IS NOT JSON\n");
                await client2.WriteAsync(garbageBytes, 0, garbageBytes.Length);
                await client2.FlushAsync();

                using var reader = new StreamReader(client2, Encoding.UTF8);
                string? line = null;
                try
                {
                    line = await reader.ReadLineAsync();
                }
                catch (IOException)
                {
                    // Server closed the pipe cleanly
                }
                Assert.True(string.IsNullOrEmpty(line), "Expected null or empty response for garbage request");
            }

            // 3. Oversized message (>64 KB) gets rejected / dropped
            using (var client3 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client3.ConnectAsync(3000);
                // Send 70 KB > 64 KB cap without newline
                byte[] oversizedBytes = new byte[70 * 1024];
                Array.Fill(oversizedBytes, (byte)'X');
                try
                {
                    await client3.WriteAsync(oversizedBytes, 0, oversizedBytes.Length);
                    await client3.FlushAsync();

                    using var reader = new StreamReader(client3, Encoding.UTF8);
                    string? line = await reader.ReadLineAsync();
                    Assert.True(string.IsNullOrEmpty(line), "Expected server to drop connection on oversized message");
                }
                catch (IOException)
                {
                    // Server dropped the connection early when cap was reached
                }
            }

            // Allow pipe server to accept next connection
            await Task.Delay(100);

            // 4. Server still answers Ping afterwards
            using (var client4 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                await client4.ConnectAsync(3000);
                byte[] pingBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Request { Command = CommandNames.Ping }) + "\n");
                await client4.WriteAsync(pingBytes, 0, pingBytes.Length);
                await client4.FlushAsync();

                using var reader = new StreamReader(client4, Encoding.UTF8);
                string? line = await reader.ReadLineAsync();
                Assert.NotNull(line);
                var resp = JsonSerializer.Deserialize<Response>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(resp);
                Assert.True(resp.Ok);
                Assert.Equal("pong", resp.Data);
            }
        }
        finally
        {
            cts.Cancel();
            await server.StopAsync(CancellationToken.None);
        }
    }
}
