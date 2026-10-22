namespace Titlape.Core;

/// <summary>Clock abstraction so CRDT timestamps and tests are deterministic.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class ManualClock : IClock
{
    public ManualClock(DateTimeOffset start) => UtcNow = start;
    public DateTimeOffset UtcNow { get; private set; }
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>Network state of an edge node. Toggled by connectivity probes or by operators.</summary>
public sealed class ConnectivityState
{
    private volatile bool _online;
    public ConnectivityState(bool online) => _online = online;
    public bool IsOnline => _online;
    public string BackhaulKind { get; set; } = "3G";
    public event Action<bool>? Changed;
    public void Set(bool online)
    {
        if (_online == online) return;
        _online = online;
        Changed?.Invoke(online);
    }
}

/// <summary>Power state of a solar hub; edge nodes degrade gracefully when battery is low.</summary>
public sealed class PowerState
{
    public double BatteryPercent { get; set; } = 82;
    public bool SolarCharging { get; set; } = true;
    public bool LowPowerMode => BatteryPercent < 25;
}

/// <summary>Simple result type used across layers.</summary>
public readonly record struct Result<T>(bool Ok, T? Value, string? Error)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Fail(string error) => new(false, default, error);
}

public static class TextUtil
{
    private static readonly char[] Separators =
        " \t\r\n.,;:!?()[]{}\"'`/\\|-_+=*&^%$#@~<>".ToCharArray();

    public static string[] Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Approximate token count used for throughput metrics (≈ words + punctuation).</summary>
    public static int ApproxTokenCount(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);
}
