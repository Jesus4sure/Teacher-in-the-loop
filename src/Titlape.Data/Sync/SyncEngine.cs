using System.Diagnostics;
using Titlape.Core;
using Titlape.CrossCutting;
using Titlape.CrossCutting.Communication;

namespace Titlape.Data.Sync;

public sealed record SyncReport(
    DateTimeOffset At,
    bool Performed,
    string? SkippedReason,
    int DocsPushed,
    int DocsPulled,
    int BinaryBytes,
    int CompressedBytes,
    int JsonEquivalentBytes,
    int WireBytes,
    bool CompressionUsed,
    MergeReport PushMerge,
    MergeReport PullMerge,
    double EstimatedTransferSeconds)
{
    public double PayloadReductionVsJson => JsonEquivalentBytes == 0 ? 0 : 1.0 - (double)WireBytes / JsonEquivalentBytes;
    public int ConflictsResolved => PushMerge.ConcurrentConflicts + PullMerge.ConcurrentConflicts;
}

/// <summary>
/// Background delta-sync between an edge replica and the cloud replica. Runs only when connectivity
/// permits; payloads are protobuf-encoded and Brotli-compressed when that makes them smaller or when the
/// payload would exceed the 100KB budget (the design refinement trigger in Section 7).
/// </summary>
public sealed class SyncEngine
{
    private readonly ReplicaStore _edge;
    private readonly ReplicaStore _cloud;
    private readonly ConnectivityState _net;
    private readonly MessageBus? _bus;
    private Hlc _pushedWatermark = Hlc.Zero;
    private Hlc _pulledWatermark = Hlc.Zero;
    private readonly List<SyncReport> _history = new();
    private readonly object _gate = new();

    public const int PayloadBudgetBytes = 100 * 1024;
    public double BackhaulKbps { get; set; } = 384; // typical 3G uplink
    public bool AlwaysCompress { get; set; }

    public SyncEngine(ReplicaStore edge, ReplicaStore cloud, ConnectivityState net, MessageBus? bus = null)
    {
        _edge = edge; _cloud = cloud; _net = net; _bus = bus;
    }

    public IReadOnlyList<SyncReport> History { get { lock (_gate) return _history.ToList(); } }

    public SyncReport SyncNow()
    {
        using var activity = TitlapeTelemetry.Source.StartActivity("edge.sync", ActivityKind.Client);
        lock (_gate)
        {
            if (!_net.IsOnline)
            {
                var skipped = new SyncReport(DateTimeOffset.UtcNow, false, "offline — changes cached locally", 0, 0, 0, 0, 0, 0,
                    false, MergeReport.Empty, MergeReport.Empty, 0);
                _history.Add(skipped);
                return skipped;
            }

            // PUSH edge → cloud
            var (pushDocs, pushWm) = _edge.DeltaSince(_pushedWatermark, _cloud.NodeId);
            var pushBin = SyncCodec.EncodeDelta(_edge.NodeId, pushDocs);
            var pushJson = SyncCodec.EncodeDeltaAsJson(_edge.NodeId, pushDocs);
            var (pushWire, pushCompressed) = Frame(pushBin);
            var pushMerge = _cloud.Apply(SyncCodec.DecodeDelta(Unframe(pushWire, pushCompressed)).Docs);
            _pushedWatermark = pushWm;

            // PULL cloud → edge
            var (pullDocs, pullWm) = _cloud.DeltaSince(_pulledWatermark, _edge.NodeId);
            var pullBin = SyncCodec.EncodeDelta(_cloud.NodeId, pullDocs);
            var pullJson = SyncCodec.EncodeDeltaAsJson(_cloud.NodeId, pullDocs);
            var (pullWire, pullCompressed) = Frame(pullBin);
            var pullMerge = _edge.Apply(SyncCodec.DecodeDelta(Unframe(pullWire, pullCompressed)).Docs);
            _pulledWatermark = pullWm;
            // Note: elements merged from the cloud are re-stamped locally and echo back once on the
            // next push; the echo is idempotent (no change on the cloud) so propagation terminates.

            int wire = pushWire.Length + pullWire.Length;
            var report = new SyncReport(DateTimeOffset.UtcNow, true, null, pushDocs.Count, pullDocs.Count,
                pushBin.Length + pullBin.Length,
                (pushCompressed ? pushWire.Length : 0) + (pullCompressed ? pullWire.Length : 0),
                pushJson.Length + pullJson.Length, wire, pushCompressed || pullCompressed, pushMerge, pullMerge,
                wire * 8 / 1000.0 / BackhaulKbps);
            _history.Add(report);
            TitlapeTelemetry.SyncPayloadBytes.Record(wire);
            activity?.SetTag("sync.wire_bytes", wire);
            activity?.SetTag("sync.conflicts", report.ConflictsResolved);
            _bus?.Publish($"titlape/{_edge.NodeId}/sync/completed", BitConverter.GetBytes(wire), qos: 1);
            return report;
        }
    }

    private (byte[] Wire, bool Compressed) Frame(byte[] bin)
    {
        if (bin.Length == 0) return (bin, false);
        if (AlwaysCompress || bin.Length > 512)
        {
            var c = PayloadCompression.Compress(bin);
            if (c.Length < bin.Length || bin.Length > PayloadBudgetBytes) return (c, true);
        }
        return (bin, false);
    }

    private static byte[] Unframe(byte[] wire, bool compressed) => compressed ? PayloadCompression.Decompress(wire) : wire;
}
