using System.IO;
using System.Net;
using System.Collections.Concurrent;
using Stratoscope.Core.Engines.Custom;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Tests;

// Ported from StrataCacheLogTests in the Swift tests.
public class StrataCacheLogTests
{
    private static string Parked(int tokens, int total, int bytes = 1000) =>
        "conversation cache: parked " + tokens + " tokens in 12.3 ms; parked=" + total + " bytes=" + bytes + " evictions=0 snapshot_bytes=" + bytes;

    [Fact]
    public void ReadsAGrowingFileWhileTheEngineKeepsItOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), "strata-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(writer) { AutoFlush = true })
            {
                var log = new StrataCacheLog();
                w.WriteLine("noise");
                w.WriteLine(Parked(1000, 1));
                log.Update(path);
                Assert.True(log.Readable);
                Assert.Single(log.Entries);
                Assert.Null(log.Entries[0].ParkedAt);          // read as history
                w.WriteLine(Parked(2000, 2));
                log.Update(path);
                Assert.Equal(2, log.Entries.Count);
                Assert.NotNull(log.Entries[1].ParkedAt);       // seen live
                w.Write("conversation cache: parked 9");       // a half-written line is left for the next read
                log.Update(path);
                Assert.Equal(2, log.Entries.Count);
            }
        }
        finally { File.Delete(path); }
    }

public sealed class HttpJsonSourceTests : IDisposable
{
    private readonly HttpListener listener = new HttpListener();
    private readonly string prefix;
    private readonly ConcurrentQueue<string> seen = new();
    private Func<string, (int Status, string Body)>? respond;

    public HttpJsonSourceTests()
    {
        var port = FreePort();
        prefix = "http://127.0.0.1:" + port;
        listener.Prefixes.Add(prefix + "/");
        listener.Start();
        Task.Run(Serve);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = listener.GetContext(); } catch (Exception) { return; }
            seen.Enqueue(c.Request.HttpMethod + " " + c.Request.Url!.AbsolutePath);
            var path = c.Request.Url.AbsolutePath;
            if (path == "/slow") { Thread.Sleep(1500); }
            var (status, body) =
                Volatile.Read(ref respond)?.Invoke(path) ??
                (path == "/ok" ? (200, "{\"engine\":{\"model\":\"m\"},\"n\":1.5}") :
                path == "/limit" ? (429, "{\"error\":\"too many\"}") :
                path == "/junk" ? (200, "<html>not json</html>") :
                path == "/array" ? (200, "[1,2]") :
                path == "/slow" ? (200, "{}") : (404, "{}"));
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = status;
            if (path == "/api/events") c.Response.ContentType = "text/event-stream";
            try { c.Response.OutputStream.Write(bytes, 0, bytes.Length); c.Response.Close(); } catch (Exception) { }
        }
    }

    public void Dispose() { listener.Close(); }

    [Fact]
    public async Task RouterEventsRefreshOnlyForModelAndInflightChanges()
    {
        Volatile.Write(ref respond, path => path == "/api/events" ? (200,
            "data: {\"type\":\"uiConfig\"}\r\n\r\n" +
            "data: broken-json\r\n\r\n" +
            "data: {\"type\":\"modelStatus\",\"data\":\"[]\"}\r\n\r\n" +
            "data: {\"type\":\"activity\"}\r\n\r\n" +
            "data: {\"type\":\"inflight\",\"data\":\"[]\"}\r\n\r\n") : (404, "{}"));
        using var source = new HttpJsonSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var refreshes = 0;
        await source.WatchRouterEventsAsync(Endpoints.Join(prefix, "api", "events"), () =>
        {
            if (++refreshes == 2) deadline.Cancel();
        }, deadline.Token);
        Assert.Equal(2, refreshes);
        Assert.Equal(new[] { "GET /api/events" }, seen.ToArray());
    }

    [Theory]
    [InlineData(200, """{"running":[{"model":"six-expert","state":"starting"}]}""", EngineStatus.Loading)]
    [InlineData(200, """{"running":[{"model":"swift","state":"stopping"}]}""", EngineStatus.Loading)]
    [InlineData(200, """{"running":[]}""", EngineStatus.NoModel)]
    [InlineData(200, """{"running":[{"state":"ready"}]}""", EngineStatus.NoModel)]
    [InlineData(200, "invalid JSON", null)]
    [InlineData(503, "{}", null)]
    public async Task RoutedLightTicksNeverReadTheOldModel(int status, string running, EngineStatus? expected)
    {
        Func<(int, string)> discovery = () => (200, """{"running":[{"model":"swift","state":"ready"}]}""");
        Volatile.Write(ref respond, path => path switch
        {
            "/running" => discovery(),
            "/upstream/swift/health" => (200, """{"status":"ok"}"""),
            "/upstream/swift/metrics" => (200, "q27:prefill_tokens_total 10\nq27:generation_tokens_total 20\n"),
            "/upstream/swift/ui/state" => (404, "{}"),
            _ => (404, "{}"),
        });
        using var source = new HttpJsonSource();
        var monitor = new ServerMonitor("Test router", new CustomProvider(new EngineConfig { Router = prefix }, source));
        await monitor.PollAsync(CancellationToken.None);
        Assert.NotNull(monitor.Snapshot);
        Assert.Equal("swift", monitor.Snapshot.Model);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery = () =>
        {
            started.SetResult();
            resume.Task.Wait(TimeSpan.FromSeconds(2));
            return (status, running);
        };
        var inFlight = monitor.PollAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await monitor.PollAsync(CancellationToken.None));
            var sample = monitor.SampleAsync(CancellationToken.None);
            Assert.True(sample.IsCompletedSuccessfully);
            Assert.False(await sample);
        }
        finally { resume.SetResult(); }
        await inFlight;
        await monitor.SampleAsync(CancellationToken.None);

        Assert.Equal(new[] { "GET /running", "GET /upstream/swift/health", "GET /upstream/swift/metrics",
            "GET /upstream/swift/ui/state", "GET /running" }, seen.ToArray());
        Assert.Equal(expected, monitor.Snapshot?.Status);
    }

    [Fact]
    public async Task RoutedCountersUseFreshFullPollsAndDoNotCrossModelIds()
    {
        var model = "swift";
        var token = 10;
        var metricStatus = 200;
        Volatile.Write(ref respond, path => path switch
        {
            "/running" => (200, $$"""{"running":[{"model":"{{model}}","name":"Same title","state":"ready"}]}"""),
            "/upstream/swift/health" or "/upstream/six-expert/health" => (200, """{"status":"ok"}"""),
            "/upstream/swift/metrics" or "/upstream/six-expert/metrics" => (metricStatus,
                FormattableString.Invariant($"q27:prefill_tokens_total {token}\nq27:generation_tokens_total {token}\nq27:prefill_busy_seconds_total {token / 10.0}\nq27:decode_busy_seconds_total {token / 10.0}\nq27:decode_step_tokens_total {token}\n")),
            _ => (404, "{}"),
        });
        using var source = new HttpJsonSource();
        var provider = new CustomProvider(new EngineConfig { Router = prefix }, source);
        var history = new SpeedHistory();
        var start = DateTime.UnixEpoch.AddMinutes(1);
        var first = await provider.PollAsync(CancellationToken.None);
        Assert.Equal("Same title", first!.Model);
        Assert.Equal(new EngineCounters(10, 10, 1, 1, 10), first.FullPollCounters);
        history.Add(start, first);
        Assert.Equal(1, history.Count);

        token = 30;
        var second = await provider.PollAsync(CancellationToken.None);
        history.Add(start.AddSeconds(2), second);
        Assert.Equal(2, history.Count);
        Assert.Equal(10, history.Latest(HistoryCurve.Decode));
        Assert.Equal(20, history.Latest(HistoryCurve.Total));
        Assert.Equal(10, history.Average(HistoryCurve.Decode, SpeedHistory.Seconds(start.AddSeconds(2))));
        Assert.Equal(20, history.Average(HistoryCurve.Total, SpeedHistory.Seconds(start.AddSeconds(2))));

        for (var i = 0; i < 4; i++) Assert.Null(await provider.ReadCountersAsync(CancellationToken.None));
        Assert.Equal(2, seen.Count(request => request == "GET /upstream/swift/metrics"));
        Assert.Equal(2, history.Count);

        metricStatus = 429;
        var limited = await provider.PollAsync(CancellationToken.None);
        Assert.True(limited!.RouterLimited);
        Assert.Null(limited.FullPollCounters);
        history.Add(start.AddSeconds(4), limited);
        Assert.Equal(2, history.Count);

        model = "six-expert";
        var limitedNew = await provider.PollAsync(CancellationToken.None);
        Assert.False(limitedNew!.RouterLimited);
        Assert.Equal(EngineStatus.Loading, limitedNew.Status);
        Assert.Null(limitedNew.FullPollCounters);

        metricStatus = 200;
        token = 100_000;
        var changed = await provider.PollAsync(CancellationToken.None);
        Assert.Equal("Same title", changed!.Model);
        Assert.Equal(100_000, changed.FullPollCounters!.GeneratedTokens);
        history.Add(start.AddSeconds(6), changed);
        Assert.Equal(3, history.Count);
        Assert.True(double.IsNaN(history.Average(HistoryCurve.Decode, SpeedHistory.Seconds(start.AddSeconds(6)))));
        Assert.True(double.IsNaN(history.Average(HistoryCurve.Total, SpeedHistory.Seconds(start.AddSeconds(6)))));
        Assert.Equal(3, seen.Count(request => request == "GET /upstream/swift/metrics"));
        Assert.Equal(2, seen.Count(request => request == "GET /upstream/six-expert/metrics"));
    }

    [Fact]
    public async Task CancelledRoutedPollDoesNotReplayThePreviousSample()
    {
        using var cancel = new CancellationTokenSource();
        var requests = 0;
        Volatile.Write(ref respond, path => path switch
        {
            "/running" => (200, """{"running":[{"model":"swift","state":"ready"}]}"""),
            "/upstream/swift/health" => (200, """{"status":"ok"}"""),
            "/upstream/swift/metrics" => Interlocked.Increment(ref requests) == 1
                ? (200, "q27:prefill_tokens_total 10\nq27:generation_tokens_total 20\n") : CancelMetrics(),
            _ => (404, "{}"),
        });
        using var source = new HttpJsonSource();
        var monitor = new ServerMonitor("Test router", new CustomProvider(new EngineConfig { Router = prefix }, source));
        await monitor.PollAsync(CancellationToken.None);
        var snapshot = monitor.Snapshot;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.PollAsync(cancel.Token));
        Assert.Same(snapshot, monitor.Snapshot);
        Assert.False(await monitor.SampleAsync(CancellationToken.None));
        Assert.Equal(2, requests);

        Assert.Equal(2, monitor.History.Count);
        Assert.True(double.IsNaN(monitor.History.Latest(HistoryCurve.Decode)));

        (int, string) CancelMetrics()
        {
            cancel.Cancel();
            return (200, "q27:prefill_tokens_total 30\nq27:generation_tokens_total 40\n");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredDirectEndpointWorksOnItsOwnAndAsAFallback(bool useRouter)
    {
        Volatile.Write(ref respond, path => path switch
        {
            "/running" => (503, "{}"),
            "/health" => (200, """{"status":"ok"}"""),
            "/metrics" => (200, "q27:prefill_tokens_total 10\nq27:generation_tokens_total 20\n"),
            _ => (404, "{}"),
        });
        using var source = new HttpJsonSource();
        var provider = new CustomProvider(new EngineConfig { Router = useRouter ? prefix : null, Url = prefix }, source);
        var snapshot = await provider.PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Idle, snapshot!.Status);
        var light = await provider.ReadCountersAsync(CancellationToken.None);
        var counters = new EngineCounters(10, 20);
        if (useRouter)
        {
            Assert.Equal(counters, snapshot.FullPollCounters);
            Assert.Null(light);
            Assert.Equal(new[] { "GET /running", "GET /health", "GET /metrics", "GET /ui/state" }, seen.ToArray());
        }
        else
        {
            Assert.Null(snapshot.FullPollCounters);
            Assert.Equal(counters, light);
            Assert.Equal(new[] { "GET /health", "GET /metrics", "GET /ui/state", "GET /metrics" }, seen.ToArray());
        }
    }

    [Fact]
    public async Task Reports429AsAStatus()
    {
        using (var source = new HttpJsonSource())
        {
            var r = await source.GetAsync(new Uri(prefix + "/limit"), Json.MetricsType, CancellationToken.None);
            Assert.False(r.Ok);
            Assert.Equal(429, r.Status);
            Assert.Equal(FetchFailure.Status, r.Failure);
        }
    }

}
}
