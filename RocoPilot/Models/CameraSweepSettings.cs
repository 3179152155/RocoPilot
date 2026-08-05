namespace RocoPilot.Models;

/// <summary>
/// Controls the horizontal camera sweep performed by <see cref="RocoPilot.Services.CameraSweepService"/>.
/// Values are intentionally bounded so an accidental setting cannot create an unsafe input loop.
/// </summary>
public sealed class CameraSweepSettings
{
    public const int DefaultPixelsPerTick = 2;
    public const int DefaultMovementIntervalMs = 10;
    public const int DefaultDirectionDurationSeconds = 5;

    public const int MinimumPixelsPerTick = 1;
    public const int MaximumPixelsPerTick = 100;
    public const int MinimumMovementIntervalMs = 5;
    public const int MaximumMovementIntervalMs = 1000;
    public const int MinimumDirectionDurationSeconds = 1;
    public const int MaximumDirectionDurationSeconds = 3600;

    public int PixelsPerTick
    {
        get;
        set;
    } = DefaultPixelsPerTick;

    public int MovementIntervalMs
    {
        get;
        set;
    } = DefaultMovementIntervalMs;

    public int DirectionDurationSeconds
    {
        get;
        set;
    } = DefaultDirectionDurationSeconds;

    /// <summary>
    /// The direction used when a new sweep starts. A sweep reverses automatically
    /// after <see cref="DirectionDurationSeconds"/>.
    /// </summary>
    public CameraSweepDirection InitialDirection
    {
        get;
        set;
    } = CameraSweepDirection.Right;

    public static CameraSweepSettings CreateDefault() => new();

    public CameraSweepSettings Clone()
    {
        return new CameraSweepSettings
        {
            PixelsPerTick = PixelsPerTick,
            MovementIntervalMs = MovementIntervalMs,
            DirectionDurationSeconds = DirectionDurationSeconds,
            InitialDirection = InitialDirection
        };
    }

    public static CameraSweepSettings Normalize(CameraSweepSettings? settings)
    {
        var normalized = settings?.Clone() ?? CreateDefault();
        normalized.PixelsPerTick = Math.Clamp(
            normalized.PixelsPerTick,
            MinimumPixelsPerTick,
            MaximumPixelsPerTick);
        normalized.MovementIntervalMs = Math.Clamp(
            normalized.MovementIntervalMs,
            MinimumMovementIntervalMs,
            MaximumMovementIntervalMs);
        normalized.DirectionDurationSeconds = Math.Clamp(
            normalized.DirectionDurationSeconds,
            MinimumDirectionDurationSeconds,
            MaximumDirectionDurationSeconds);
        if (!Enum.IsDefined(normalized.InitialDirection))
        {
            normalized.InitialDirection = CameraSweepDirection.Right;
        }

        return normalized;
    }
}

public enum CameraSweepDirection
{
    Left = -1,
    Right = 1
}
