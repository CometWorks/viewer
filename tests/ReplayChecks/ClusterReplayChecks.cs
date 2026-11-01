using System.Collections.Concurrent;
using System.Text.Json;
using CometWorks.EntityViewer.Quasar.Replay;
using CometWorks.EntityViewer.Replay;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Manifests;

internal static class ClusterReplayChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task Run(string root)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var channel = new ClusterChannel(now);
        var a = Node("a-1", "cluster-a", "slot-1");
        var b = Node("a-2", "cluster-a", "slot-2");
        var other = Node("b-1", "cluster-b", "slot-1");
        var standalone = new QuasarCompanionServer("standalone", "standalone-agent", "Same name");
        channel.Servers = [a, b, other, standalone];
        var context = new QuasarPluginContext
        {
            InstallDirectory = Path.Combine(root, "clusters"), PluginDirectory = root, CacheDirectory = root,
            Configuration = new ConfigurationBuilder().Build(), Environment = new TestEnvironment(),
            Manifest = new QuasarPluginManifest { Id = "test", DisplayName = "test", Version = "test", EntryAssembly = "test", EntryType = "test", ProjectPath = "test" }
        };
        using var history = new ReplayHistoryService(context, channel, NullLogger<ReplayHistoryService>.Instance);
        await history.StartAsync(default);
        try
        {
            await Until(() => history.Servers().Length == 3 && history.Resolve(b.AgentId) == "cluster:cluster-a", "Discovery must group nodes by cluster identity, not shared display names");
            Check(channel.Requests.IsEmpty, "Cluster discovery must preserve default-disabled recording");
            var list = JsonSerializer.SerializeToElement(history.Servers(), Json);
            var cluster = list.EnumerateArray().Single(s => s.GetProperty("serverId").GetString() == "cluster:cluster-a");
            Check(cluster.GetProperty("displayName").GetString() == "Same name", "Unified cluster must use its display name without a node suffix");
            Check(cluster.GetProperty("memberIds").GetArrayLength() == 4, "Every node and agent alias must be retained for deep links");
            Check(history.Resolve(a.AgentId) == "cluster:cluster-a" && history.Resolve(b.ServerId) == "cluster:cluster-a", "Node links must resolve to the unified server");

            await history.SetRecording(b.AgentId, new() { Enabled = true, MaxIncidentMinutes = 30, MaxContextRadius = 5000 }, default);
            Check(channel.Requests[a.AgentId].Record && channel.Requests[b.AgentId].Record, "Recording control must reach every cluster node through its exact agent");
            Check(!channel.Requests.ContainsKey(other.AgentId) && !channel.Requests.ContainsKey(standalone.AgentId), "Cluster control must not touch another cluster or a standalone server");
            var batches = await history.Query(a.ServerId, now - 5000, now, default);
            Check(batches.Count == 2 && batches.Select(x => x.Node).ToHashSet().SetEquals(["slot-1", "slot-2"]), "Unified queries must include both node sessions and stable slots");
            Check(batches.Select(x => x.Session).Distinct().Count() == 2, "Concurrent sessions must not be collapsed");
            var overview = JsonSerializer.SerializeToElement(history.Overview("cluster:cluster-a"), Json);
            Check(overview.GetProperty("segments").GetArrayLength() == 2 && overview.GetProperty("incidents").GetArrayLength() == 2, "Cluster overview must include segments and incidents from every node");

            var joined = Node("a-3", "cluster-a", "slot-3");
            channel.Servers = [a, b, joined, other, standalone];
            await Until(() => channel.Requests.TryGetValue(joined.AgentId, out var request) && request.Record, "A newly joined node must inherit the saved cluster policy");
            Check(channel.Requests[joined.AgentId].Settings.MaxContextRadius == 5000, "New nodes inherit incident limits too");
            channel.Servers = [a, joined, other, standalone]; // One member goes offline before stopping.
            await history.SetRecording("cluster:cluster-a", new() { Enabled = false, MaxIncidentMinutes = 20 }, default);
            Check(!channel.Requests[a.AgentId].Record && !channel.Requests[joined.AgentId].Record, "Cluster stop must reach all connected nodes");
            Check(!JsonSerializer.SerializeToElement(history.Overview(b.AgentId), Json).GetProperty("recordingEnabled").GetBoolean(), "Offline members must retain the cluster stop policy");
        }
        finally { await history.StopAsync(default); }

        channel.Servers = [];
        using var restarted = new ReplayHistoryService(context, channel, NullLogger<ReplayHistoryService>.Instance);
        await restarted.StartAsync(default);
        try
        {
            await Until(() => restarted.Servers().Length == 3, "Cluster grouping must survive restart without live nodes");
            var overview = JsonSerializer.SerializeToElement(restarted.Overview(a.AgentId), Json);
            Check(!overview.GetProperty("recordingEnabled").GetBoolean() && overview.GetProperty("settings").GetProperty("maxIncidentMinutes").GetInt32() == 20,
                "Cluster recording opt-out and limits must survive restart");
            Check((await restarted.Query("cluster:cluster-a", now - 5000, now, default)).Count == 3, "Retained history from disconnected nodes must remain unified");
            var replacement = Node("a-4", "cluster-a", "slot-1");
            channel.Servers = [replacement];
            await Until(() => JsonSerializer.SerializeToElement(restarted.Servers(), Json).EnumerateArray().Any(s => s.GetProperty("memberIds").EnumerateArray().Any(id => id.GetString() == replacement.AgentId)),
                "Replacement node must join the existing logical cluster");
            Check(!channel.Requests.ContainsKey(replacement.AgentId), "Replacement incarnations must not resume recording after a persisted cluster stop");
        }
        finally { await restarted.StopAsync(default); }
        Console.WriteLine("Cluster discovery, aliases, recording policy, new nodes, multi-node history and offline restart checks passed.");
    }

    private static QuasarCompanionServer Node(string id, string cluster, string slot) => new(id, id + "-agent", "Same name / " + slot)
        { ClusterId = cluster, ClusterDisplayName = "Same name", ClusterSlot = slot };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(predicate(), message);
    }

    private sealed class ClusterChannel(long now) : IQuasarCompanionChannel
    {
        public QuasarCompanionServer[] Servers = [];
        public readonly ConcurrentDictionary<string, ReplayReadRequest> Requests = new();
        private readonly ConcurrentDictionary<string, ReplayChunk> _chunks = new();
        public IReadOnlyList<QuasarCompanionServer> GetConnectedServers() => Servers;
        public Task<TResponse> SendAsync<TRequest, TResponse>(string serverId, string companionPluginId, string operation, TRequest request, CancellationToken cancellationToken)
        {
            Check(Servers.Any(s => s.AgentId == serverId), "Companion commands must target an exact connected agent, never the cluster alias");
            var read = (ReplayReadRequest)(object)request!;
            Requests[serverId] = read;
            var chunk = _chunks.GetOrAdd(serverId, id =>
            {
                var incidentId = Guid.NewGuid().ToString("N");
                return new ReplayChunk { Id = ReplayChunkStore.NewId(now - 2000, 1), Data = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(
                    new ReplayBatch { Session = Guid.NewGuid().ToString("N"), Records = [
                        new() { At = now - 2000, Kind = "health", Value = 1 },
                        new() { At = now - 2000, Kind = "sample", Entity = id, Position = [1, 2, 3] },
                        new() { At = now - 2000, Kind = "incident", IncidentId = incidentId, Incident = new() { Id = incidentId,
                            StartedAt = now - 2000, LastActivityAt = now - 2000, Until = now + 58000, Deadline = now + 898000,
                            Seed = [0, 0, 0], Radius = 500, MaxRadius = 10000 } }
                    ] }, Json)) };
            });
            return Task.FromResult((TResponse)(object)new ReplayPage { Chunks = read.Acknowledge.Contains(chunk.Id) ? [] : [chunk] });
        }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ClusterChecks";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
