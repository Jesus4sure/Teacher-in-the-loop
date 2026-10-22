using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;

namespace Titlape.CrossCutting.Communication;

public sealed record BusMessage(string Topic, byte[] Payload, int QoS, string? TraceParent, DateTimeOffset At);

/// <summary>
/// Lightweight MQTT-semantics pub/sub (topic levels, '+' and '#' wildcards, QoS 0/1, retained
/// messages). Runs in-process on an edge node; the same topic tree is bridged to a cloud MQTT broker
/// when connectivity permits. Every message carries W3C trace context for OpenTelemetry propagation.
/// </summary>
public sealed class MessageBus
{
    private readonly ConcurrentDictionary<Guid, (string Filter, Action<BusMessage> Handler)> _subs = new();
    private readonly ConcurrentDictionary<string, BusMessage> _retained = new();
    private long _bytesPublished;
    private long _messagesPublished;

    public long BytesPublished => Interlocked.Read(ref _bytesPublished);
    public long MessagesPublished => Interlocked.Read(ref _messagesPublished);

    public IDisposable Subscribe(string topicFilter, Action<BusMessage> handler)
    {
        var id = Guid.NewGuid();
        _subs[id] = (topicFilter, handler);
        foreach (var r in _retained.Values.Where(m => Matches(topicFilter, m.Topic))) handler(r);
        return new Unsubscriber(() => _subs.TryRemove(id, out _));
    }

    public void Publish(string topic, byte[] payload, int qos = 0, bool retain = false)
    {
        if (topic.Contains('+') || topic.Contains('#')) throw new ArgumentException("Wildcards are not allowed in publish topics.");
        var msg = new BusMessage(topic, payload, qos, TraceContext.Current(), DateTimeOffset.UtcNow);
        Interlocked.Add(ref _bytesPublished, payload.Length);
        Interlocked.Increment(ref _messagesPublished);
        if (retain) _retained[topic] = msg;
        foreach (var (filter, handler) in _subs.Values)
        {
            if (!Matches(filter, topic)) continue;
            try { handler(msg); }
            catch (Exception ex) when (qos == 0) { Trace.TraceWarning($"QoS0 delivery dropped: {ex.Message}"); }
        }
    }

    /// <summary>MQTT topic-filter matching.</summary>
    public static bool Matches(string filter, string topic)
    {
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true;
            if (i >= t.Length) return false;
            if (f[i] == "+") continue;
            if (!string.Equals(f[i], t[i], StringComparison.Ordinal)) return false;
        }
        return f.Length == t.Length;
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>W3C traceparent propagation helpers (OpenTelemetry context across headers/messages).</summary>
public static class TraceContext
{
    public static string? Current() => Activity.Current?.Id;

    public static ActivityContext? Parse(string? traceParent) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, out var ctx) ? ctx : null;
}

/// <summary>Measures payload efficiency for RQ 5.2 (target &lt; 100KB per sync over 3G).</summary>
public static class PayloadCompression
{
    public static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data);
        return ms.ToArray();
    }

    public static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var br = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        br.CopyTo(output);
        return output.ToArray();
    }
}
