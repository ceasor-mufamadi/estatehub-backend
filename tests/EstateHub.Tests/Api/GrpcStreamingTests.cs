using System.Threading.Channels;
using EstateHub.Api;
using EstateHub.Api.Streaming;
using FluentAssertions;
using Grpc.Core;

namespace EstateHub.Tests.Api;

public class GrpcStreamingTests
{
    [Fact]
    public async Task Sends_Connected_Heartbeat_Then_Events_In_Order()
    {
        var writer = new RecordingWriter();

        await GrpcStreaming.PumpAsync(
            Items("one", "two"), writer, TestServerCallContext.Create(),
            toMessage: s => new AuditLogStreamEvent { Entry = new AuditLogEntry { Action = s } },
            heartbeat: hb => new AuditLogStreamEvent { Heartbeat = hb },
            TimeSpan.FromMinutes(1));

        writer.Messages.Should().HaveCount(3);
        writer.Messages[0].Heartbeat.Reason.Should().Be(GrpcStreaming.ConnectedReason);
        writer.Messages[1].Entry.Action.Should().Be("one");
        writer.Messages[2].Entry.Action.Should().Be("two");
    }

    [Fact]
    public async Task Sends_Keepalive_Heartbeats_While_Idle()
    {
        var writer = new RecordingWriter();
        var source = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource();

        var pump = GrpcStreaming.PumpAsync(
            source.Reader.ReadAllAsync(cts.Token), writer, TestServerCallContext.Create(cts.Token),
            toMessage: s => new AuditLogStreamEvent { Entry = new AuditLogEntry { Action = s } },
            heartbeat: hb => new AuditLogStreamEvent { Heartbeat = hb },
            TimeSpan.FromMilliseconds(50));

        await WaitUntilAsync(() => writer.Messages.Count(m => m.Heartbeat?.Reason == GrpcStreaming.KeepaliveReason) >= 2);
        await cts.CancelAsync();
        await pump;

        writer.Messages.Should().Contain(m => m.Heartbeat != null && m.Heartbeat.Reason == GrpcStreaming.KeepaliveReason);
    }

    [Fact]
    public async Task Client_Disconnect_Ends_The_Stream_Quietly()
    {
        var writer = new RecordingWriter();
        using var cts = new CancellationTokenSource();

        var pump = GrpcStreaming.PumpAsync(
            Channel.CreateUnbounded<string>().Reader.ReadAllAsync(cts.Token), writer, TestServerCallContext.Create(cts.Token),
            toMessage: s => new AuditLogStreamEvent(),
            heartbeat: hb => new AuditLogStreamEvent { Heartbeat = hb },
            TimeSpan.FromMinutes(1));

        await WaitUntilAsync(() => writer.Messages.Count == 1);
        await cts.CancelAsync();

        await pump.Invoking(p => p).Should().NotThrowAsync();
    }

    private static async IAsyncEnumerable<string> Items(params string[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        condition().Should().BeTrue("the condition should be met within 5 seconds");
    }

    private sealed class RecordingWriter : IServerStreamWriter<AuditLogStreamEvent>
    {
        private readonly object _gate = new();
        private readonly List<AuditLogStreamEvent> _messages = [];

        public List<AuditLogStreamEvent> Messages { get { lock (_gate) return [.. _messages]; } }
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(AuditLogStreamEvent message) => WriteAsync(message, CancellationToken.None);

        // The real ASP.NET Core writer supports cancellation; the interface's default would throw.
        public Task WriteAsync(AuditLogStreamEvent message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
