namespace SurfTimer;

using SwiftlyS2.Shared.Natives;

internal sealed class SurfTimerConfig
{
    public string ConfigVersion { get; set; } = "0.1.0";
    public bool Debug { get; set; } = false;
    public bool EnableHud { get; set; } = true;
    public bool PreferParticleHud { get; set; } = true;
    public bool ShowMapTier { get; set; } = true;
    public int HudUpdateEveryTicks { get; set; } = 1;
    public int HudDurationMs { get; set; } = 1000;
    public int ParticleHudCharacters { get; set; } = 9;
    public float ParticleHudXStart { get; set; } = -4.0f;
    public float ParticleHudSpacing { get; set; } = 1.0f;
    public float ParticleHudYOffset { get; set; } = -2.0f;
    public float ParticleHudScale { get; set; } = 0.04f;
    public bool ParticleHudShowLabel { get; set; } = true;
    public string ParticleHudLabelText { get; set; } = "timer";
    public float ParticleHudLabelXStart { get; set; } = -2.0f;
    public float ParticleHudLabelSpacing { get; set; } = 1.0f;
    public float ParticleHudLabelYOffset { get; set; } = -2.7f;
    public float ParticleHudLabelScale { get; set; } = 0.032f;
    public float TimerHudBaseX { get; set; } = -5.8f;
    public float TimerHudBaseY { get; set; } = -12.9f;
    public float TimerHudSpacing { get; set; } = 1.0f;
    public float TimerHudScale { get; set; } = 0.026f;
    public float TimerHudTimeOffsetX { get; set; } = 2.0f;
    public float TimerHudTimeOffsetY { get; set; } = 0.0f;
    public float TimerHudSpeedOffsetX { get; set; } = 1.4f;
    public float TimerHudSpeedOffsetY { get; set; } = 1.35f;
    public float TimerHudSpeedLabelOffsetX { get; set; } = 1.4f;
    public float TimerHudSpeedLabelOffsetY { get; set; } = -3.65f;
    public float TimerHudSpeedLabelScaleMultiplier { get; set; } = 0.7f;
    public float TimerHudSpeedLabelSpacingMultiplier { get; set; } = 1.0f;
    public float TimerHudDetailOffsetX { get; set; } = -1.1f;
    public float TimerHudDetailOffsetY { get; set; } = -1.9f;
    public float TimerHudDetailScaleMultiplier { get; set; } = 0.7f;
    public int TimerHudTimeRed { get; set; } = 156;
    public int TimerHudTimeGreen { get; set; } = 255;
    public int TimerHudTimeBlue { get; set; } = 87;
    public int TimerHudSpeedLabelRed { get; set; } = 156;
    public int TimerHudSpeedLabelGreen { get; set; } = 255;
    public int TimerHudSpeedLabelBlue { get; set; } = 87;
    public int TimerHudSpeedRed { get; set; } = 226;
    public int TimerHudSpeedGreen { get; set; } = 67;
    public int TimerHudSpeedBlue { get; set; } = 255;
    public int TimerHudDetailRed { get; set; } = 156;
    public int TimerHudDetailGreen { get; set; } = 255;
    public int TimerHudDetailBlue { get; set; } = 87;
    public bool AutoScanZones { get; set; } = true;
    public float ZoneMarkerFontSize { get; set; } = 48.0f;
    public float ZoneMarkerHeightOffset { get; set; } = 96.0f;
    public string[] StartZoneNames { get; set; } = ["map_start", "map_startzone", "s1_start", "stage1_start", "timer_start", "timer_startzone", "zone_start", "start"];
    public string[] EndZoneNames { get; set; } = ["map_end", "map_endzone", "s1_end", "stage1_end", "timer_end", "timer_endzone", "zone_end", "end"];
}

internal sealed class PlayerTimerState
{
    public bool Running { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public TimeSpan LastFinish { get; set; }
    public int Track { get; set; }
    public int LastStageSequence { get; set; }
    public int LastCheckpointSequence { get; set; }
    public string LastSplitLabel { get; set; } = "";
    public TimeSpan LastSplitElapsed { get; set; }
    public DateTimeOffset LastSplitAt { get; set; }

    public void Reset()
    {
        Running = false;
        StartedAt = default;
        LastFinish = TimeSpan.Zero;
        Track = 0;
        LastStageSequence = 0;
        LastCheckpointSequence = 0;
        LastSplitLabel = "";
        LastSplitElapsed = TimeSpan.Zero;
        LastSplitAt = default;
    }
}

internal enum ZoneType
{
    Invalid,
    Start,
    End,
    Stage,
    Checkpoint,
}

internal sealed class RuntimeZone
{
    public uint EntityIndex { get; init; }
    public string TargetName { get; init; } = "";
    public ZoneType Type { get; init; }
    public int Track { get; init; }
    public int Sequence { get; init; }
    public Vector? Origin { get; init; }
    public QAngle? Angles { get; init; }
    public Vector? Mins { get; init; }
    public Vector? Maxs { get; init; }
}

internal sealed class RecordsFile
{
    public Dictionary<string, List<PlayerRecord>> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<CheckpointRecord>> Checkpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MapMetadata
{
    public int Tier { get; set; }
    public string Type { get; set; } = "";
}

internal class PlayerRecord
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    public double Seconds { get; set; }
    public string FinishedAtUtc { get; set; } = "";
}

internal sealed class CheckpointRecord : PlayerRecord
{
    public string Kind { get; set; } = "cp";
    public int Checkpoint { get; set; }
}
