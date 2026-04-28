using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Core;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace SurfTimer;

[PluginMetadata(Id = "surftimer", Version = "0.1.0", Name = "SurfTimer", Author = "Excalibro/Codex", Description = "Swiftly-native surf timer MVP")]
public sealed class SurfTimer : BasePlugin
{
    private readonly PlayerTimerState?[] _players = new PlayerTimerState?[65];
    private readonly Dictionary<uint, RuntimeZone> _zones = new();
    private SurfTimerConfig _config = new();
    private RecordsFile _records = new();
    private Dictionary<string, MapMetadata> _mapMetadata = new(StringComparer.OrdinalIgnoreCase);
    private int _hudTickCounter;
    private int _zoneScanTicker;
    private int _zoneScanAttemptsRemaining;
    private int _particleHudRenderFailureCooldownTicks;
    private object? _particleHud;
    private MethodInfo? _particleHudSetText;
    private MethodInfo? _particleHudSetGlyphs;
    private MethodInfo? _particleHudClear;
    private readonly List<CEntityInstance> _zoneMarkers = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public SurfTimer(ISwiftlyCore core) : base(core) { }

    public override void Load(bool hotReload)
    {
        LoadConfig();
        LoadRecords();
        LoadMapMetadata();
        Core.Logger.LogWarning("[SurfTimer] Loaded. HotReload={HotReload}", hotReload);
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        if (!interfaceManager.TryGetSharedInterface<object>("ParticleHud.v1", out var particleHud))
        {
            Core.Logger.LogWarning("[SurfTimer] ParticleHud.v1 not found. Falling back to CenterHTML HUD.");
            return;
        }

        _particleHud = particleHud;
        var type = particleHud.GetType();
        _particleHudSetText = type.GetMethod("SetText", BindingFlags.Instance | BindingFlags.Public);
        _particleHudSetGlyphs = type.GetMethod("SetGlyphs", BindingFlags.Instance | BindingFlags.Public);
        _particleHudClear = type.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);

        if (_particleHudSetText == null || _particleHudClear == null)
        {
            _particleHud = null;
            Core.Logger.LogWarning("[SurfTimer] ParticleHud.v1 found, but required methods are missing. Falling back to CenterHTML HUD.");
            return;
        }

        Core.Logger.LogWarning("[SurfTimer] ParticleHud.v1 acquired.");
        ScheduleTimerHudRebuild();
    }

    public override void Unload()
    {
        SaveRecords();
    }

    [EventListener<EventDelegates.OnMapLoad>]
    public void OnMapLoad(IOnMapLoadEvent @event)
    {
        ClearAllTimerHuds();
        ClearZoneMarkers();
        ResetAllPlayers();
        _zones.Clear();
        _zoneScanAttemptsRemaining = 120;
        ScheduleZoneScan();
        ScheduleTimerHudRebuild();
    }

    [EventListener<EventDelegates.OnMapUnload>]
    public void OnMapUnload(IOnMapUnloadEvent @event)
    {
        ClearAllTimerHuds();
        ClearZoneMarkers();
        SaveRecords();
        _zones.Clear();
        ResetAllPlayers();
    }

    [EventListener<EventDelegates.OnEntitySpawned>]
    public void OnEntitySpawned(IOnEntitySpawnedEvent @event)
    {
        if (@event.Entity.DesignerName != "trigger_multiple")
            return;

        TryRegisterZone(@event.Entity.As<CBaseEntity>());
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnClientConnect(EventPlayerConnectFull @event)
    {
        if (@event.UserIdPlayer is { } player && !player.IsFakeClient)
        {
            _players[player.PlayerID] = new PlayerTimerState();
            ScheduleZoneScan();
            ScheduleTimerHudRebuild(player);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnClientDisconnect(EventPlayerDisconnect @event)
    {
        if (@event.UserIdPlayer is { } player)
        {
            ClearTimerHud(player);
            _players[player.PlayerID] = null;
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is not { } player || player.IsFakeClient)
            return HookResult.Continue;

        _players[player.PlayerID] ??= new PlayerTimerState();
        ScheduleTimerHudRebuild(player);

        return HookResult.Continue;
    }

    [EventListener<EventDelegates.OnEntityDeleted>]
    public void OnEntityDeleted(IOnEntityDeletedEvent @event)
    {
        _zones.Remove(@event.Entity.Index);
    }

    [EventListener<EventDelegates.OnEntityStartTouch>]
    public void OnEntityStartTouch(IOnEntityStartTouchEvent @event)
    {
        if (!TryResolveZoneTouch(@event.Entity, @event.OtherEntity, out var zone, out var player))
            return;

        HandleZoneStartTouch(player, zone);
    }

    [EventListener<EventDelegates.OnEntityEndTouch>]
    public void OnEntityEndTouch(IOnEntityEndTouchEvent @event)
    {
        if (!TryResolveZoneTouch(@event.Entity, @event.OtherEntity, out var zone, out var player))
            return;

        HandleZoneEndTouch(player, zone);
    }

    [EventListener<EventDelegates.OnTick>]
    public void OnTick()
    {
        MaybeAutoScanZones();
        if (_particleHudRenderFailureCooldownTicks > 0)
            _particleHudRenderFailureCooldownTicks--;

        if (!_config.EnableHud)
            return;

        var everyTicks = Math.Max(1, _config.HudUpdateEveryTicks);
        if (++_hudTickCounter % everyTicks != 0)
            return;

        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (player?.IsValid != true || player.IsFakeClient)
                continue;

            var state = GetState(player);
            var elapsed = state.Running
                ? DateTimeOffset.UtcNow - state.StartedAt
                : state.LastFinish;
            RenderTimerHud(player, elapsed);
        }
    }

    [Command("r")]
    public void RestartCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        ResetPlayer(player);
    }

    [Command("restart")]
    public void RestartAliasCommand(ICommandContext context) => RestartCommand(context);

    [Command("b")]
    public void BonusCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        if (context.Args.Length == 0 || !int.TryParse(context.Args[0], out var bonus) || bonus <= 0)
        {
            player.SendChat("[gold][Timer][/] Usage: [white]!b 1[/]");
            return;
        }

        TeleportToStart(player, bonus);
    }

    [Command("bonus")]
    public void BonusAliasCommand(ICommandContext context) => BonusCommand(context);

    [Command("s")]
    public void StageCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        if (context.Args.Length == 0 || !int.TryParse(context.Args[0], out var stage) || stage <= 0)
        {
            player.SendChat("[gold][Timer][/] Usage: [white]!s 2[/]");
            return;
        }

        TeleportToZone(player, ZoneType.Stage, 0, stage, $"stage {stage}");
    }

    [Command("stage")]
    public void StageAliasCommand(ICommandContext context) => StageCommand(context);

    [Command("cp")]
    public void CheckpointCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        if (context.Args.Length == 0 || !int.TryParse(context.Args[0], out var checkpoint) || checkpoint <= 0)
        {
            player.SendChat("[gold][Timer][/] Usage: [white]!cp 1[/]");
            return;
        }

        var track = GetState(player).Track;
        TeleportToZone(player, ZoneType.Checkpoint, track, checkpoint, track == 0 ? $"checkpoint {checkpoint}" : $"bonus {track} checkpoint {checkpoint}");
    }

    [Command("stop")]
    public void StopCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        var state = GetState(player);
        if (!state.Running)
        {
            state.LastFinish = TimeSpan.Zero;
            RenderTimerHud(player, TimeSpan.Zero);
            player.SendChat("[gold][Timer][/] Timer is already stopped.");
            return;
        }

        state.Reset();
        RenderTimerHud(player, TimeSpan.Zero);
        player.SendChat("[gold][Timer][/] Stopped.");
    }

    [Command("pb")]
    public void PbCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        var state = GetState(player);
        var mapName = GetRecordMapName(state.Track);
        var record = GetPlayerBest(mapName, player.SteamID);
        if (record == null)
        {
            player.SendChat(state.Track == 0
                ? "[gold][Timer][/] No PB on this map yet."
                : $"[gold][Timer][/] No PB on bonus [white]{state.Track}[/] yet.");
            return;
        }

        player.SendChat($"[gold][Timer][/] PB: [lime]{FormatTime(TimeSpan.FromSeconds(record.Seconds))}[/]");
    }

    [Command("wr")]
    public void TopCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        var state = GetState(player);
        var mapName = GetRecordMapName(state.Track);
        if (!_records.Maps.TryGetValue(mapName, out var records) || records.Count == 0)
        {
            player.SendChat(state.Track == 0
                ? "[gold][Timer][/] No records on this map yet."
                : $"[gold][Timer][/] No records on bonus [white]{state.Track}[/] yet.");
            return;
        }

        var top = records.OrderBy(r => r.Seconds).Take(5).ToList();
        player.SendChat($"[gold][Timer][/] Top records for [white]{mapName}[/]:");
        for (var i = 0; i < top.Count; i++)
            player.SendChat($"[grey]{i + 1}.[/] [lime]{FormatTime(TimeSpan.FromSeconds(top[i].Seconds))}[/] [white]{top[i].Name}[/]");
    }

    [Command("top")]
    public void TopAliasCommand(ICommandContext context) => TopCommand(context);

    [Command("topcp")]
    public void TopCheckpointCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        if (context.Args.Length == 0 || !int.TryParse(context.Args[0], out var checkpoint) || checkpoint <= 0)
        {
            player.SendChat("[gold][Timer][/] Usage: [white]!topcp 1[/]");
            return;
        }

        SendTopCheckpoint(player, checkpoint);
    }

    [Command("topcp1")]
    public void TopCheckpoint1Command(ICommandContext context) => TopCheckpointAliasCommand(context, 1);

    [Command("topcp2")]
    public void TopCheckpoint2Command(ICommandContext context) => TopCheckpointAliasCommand(context, 2);

    [Command("topcp3")]
    public void TopCheckpoint3Command(ICommandContext context) => TopCheckpointAliasCommand(context, 3);

    [Command("topcp4")]
    public void TopCheckpoint4Command(ICommandContext context) => TopCheckpointAliasCommand(context, 4);

    [Command("topcp5")]
    public void TopCheckpoint5Command(ICommandContext context) => TopCheckpointAliasCommand(context, 5);

    [Command("topcp6")]
    public void TopCheckpoint6Command(ICommandContext context) => TopCheckpointAliasCommand(context, 6);

    [Command("topcp7")]
    public void TopCheckpoint7Command(ICommandContext context) => TopCheckpointAliasCommand(context, 7);

    [Command("topcp8")]
    public void TopCheckpoint8Command(ICommandContext context) => TopCheckpointAliasCommand(context, 8);

    [Command("topcp9")]
    public void TopCheckpoint9Command(ICommandContext context) => TopCheckpointAliasCommand(context, 9);

    [Command("topcp10")]
    public void TopCheckpoint10Command(ICommandContext context) => TopCheckpointAliasCommand(context, 10);

    private void TopCheckpointAliasCommand(ICommandContext context, int checkpoint)
    {
        if (context.Sender is not IPlayer player)
            return;

        SendTopCheckpoint(player, checkpoint);
    }

    [Command("zones")]
    public void ZonesCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        SendZonesInfo(player);
    }

    [Command("showzones")]
    public void ShowZonesCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        ScanExistingZones();
        SpawnZoneMarkers();
        player.SendChat($"[gold][Timer][/] Showing [lime]{_zoneMarkers.Count}[/] zone markers. Use [white]!hidezones[/] to remove them.");
    }

    [Command("hidezones")]
    public void HideZonesCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        ClearZoneMarkers();
        player.SendChat("[gold][Timer][/] Zone markers removed.");
    }

    [Command("timer")]
    public void TimerCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        var state = GetState(player);
        if (state.Running)
        {
            var elapsed = DateTimeOffset.UtcNow - state.StartedAt;
            player.SendChat($"[gold][Timer][/] Running: [lime]{FormatTime(elapsed)}[/]");
            return;
        }

        if (state.LastFinish != TimeSpan.Zero)
        {
            player.SendChat($"[gold][Timer][/] Last finish: [lime]{FormatTime(state.LastFinish)}[/]");
            return;
        }

        player.SendChat("[gold][Timer][/] Not running. Leave the start zone to begin.");
    }

    [Command("timerhud")]
    public void TimerHudCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player)
            return;

        if (context.Args.Length > 0 && context.Args[0].Equals("color", StringComparison.OrdinalIgnoreCase))
        {
            HandleTimerHudColorCommand(player, context.Args.Skip(1).ToArray());
            return;
        }

        if (context.Args.Length > 0 && context.Args[0].Equals("colors", StringComparison.OrdinalIgnoreCase))
        {
            SendTimerHudColors(player);
            return;
        }

        if (context.Args.Length > 0 && context.Args[0].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            SendTimerHudInfo(player);
            return;
        }

        OpenTimerHudMenu(player);
    }

    private void TryRegisterZone(CBaseEntity? entity)
    {
        if (entity == null || !entity.IsValidEntity)
            return;

        var targetName = entity.Entity?.Name ?? "";
        var zone = MatchZone(entity, targetName);
        if (zone.Type == ZoneType.Invalid)
            return;

        _zones[entity.Index] = zone;
        LogDebug("[SurfTimer] Registered zone {Name} index={Index} type={Type} track={Track} sequence={Sequence}",
            zone.TargetName, zone.EntityIndex, zone.Type, zone.Track, zone.Sequence);
    }

    private void ScheduleZoneScan()
    {
        if (!_config.AutoScanZones)
            return;

        _zoneScanAttemptsRemaining = Math.Max(_zoneScanAttemptsRemaining, 120);
        _zoneScanTicker = 0;
        Core.Scheduler.NextTick(ScanExistingZones);
        Core.Scheduler.DelayBySeconds(0.25f, ScanExistingZones);
        Core.Scheduler.DelayBySeconds(1.0f, ScanExistingZones);
        Core.Scheduler.DelayBySeconds(2.0f, ScanExistingZones);
        Core.Scheduler.DelayBySeconds(5.0f, ScanExistingZones);
    }

    private void MaybeAutoScanZones()
    {
        if (!_config.AutoScanZones || _zoneScanAttemptsRemaining <= 0)
            return;

        if (++_zoneScanTicker % 32 != 0)
            return;

        _zoneScanAttemptsRemaining--;
        ScanExistingZones();
    }

    private void ScanExistingZones()
    {
        try
        {
            var before = _zones.Count;
            foreach (var entity in Core.EntitySystem.GetAllEntitiesByDesignerName<CBaseEntity>("trigger_multiple"))
                TryRegisterZone(entity);

            var added = _zones.Count - before;
            if (added > 0 || _config.Debug)
                Core.Logger.LogWarning("[SurfTimer] Zone scan complete. Registered={Count} Added={Added}", _zones.Count, added);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Zone scan failed: {Message}", ex.Message);
        }
    }

    private void EnsureZonesReady()
    {
        if (!_config.AutoScanZones)
            return;

        if (_zones.Count == 0)
            ScheduleZoneScan();

        ScanExistingZones();
    }

    private bool TryResolveZoneTouch(CBaseEntity a, CBaseEntity b, out RuntimeZone zone, out IPlayer player)
    {
        zone = default!;
        player = default!;

        var zoneEntity = TryGetZone(a) != null ? a : TryGetZone(b) != null ? b : null;
        if (zoneEntity == null)
            return false;

        var playerEntity = ReferenceEquals(zoneEntity, a) ? b : a;
        var resolvedPlayer = FindPlayerByPawn(playerEntity);
        if (resolvedPlayer == null || resolvedPlayer.IsFakeClient)
            return false;

        var resolvedZone = TryGetZone(zoneEntity);
        if (resolvedZone == null)
            return false;

        zone = resolvedZone;
        player = resolvedPlayer;
        return true;
    }

    private RuntimeZone? TryGetZone(CBaseEntity entity)
    {
        if (_zones.TryGetValue(entity.Index, out var zone))
            return zone;

        if (entity.DesignerName != "trigger_multiple")
            return null;

        TryRegisterZone(entity);
        return _zones.TryGetValue(entity.Index, out zone) ? zone : null;
    }

    private IPlayer? FindPlayerByPawn(CBaseEntity entity)
    {
        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (player?.PlayerPawn?.IsValid != true)
                continue;

            if (player.PlayerPawn.Index == entity.Index)
                return player;
        }

        return null;
    }

    private void HandleZoneStartTouch(IPlayer player, RuntimeZone zone)
    {
        var state = GetState(player);
        switch (zone.Type)
        {
            case ZoneType.Start:
                state.Reset();
                state.Track = zone.Track;
                SendPlayerOnlyTimerChat(player, zone.Track == 0
                    ? "[gold][Timer][/] In start zone. Leave to begin."
                    : $"[gold][Timer][/] In bonus [white]{zone.Track}[/] start. Leave to begin.");
                break;
            case ZoneType.End when state.Running && state.Track == zone.Track:
                FinishRun(player, state);
                break;
            case ZoneType.Stage when state.Running && state.Track == zone.Track:
                HandleStageTouch(player, state, zone);
                break;
            case ZoneType.Checkpoint when state.Running && state.Track == zone.Track:
                HandleCheckpointTouch(player, state, zone);
                break;
        }
    }

    private void HandleZoneEndTouch(IPlayer player, RuntimeZone zone)
    {
        if (zone.Type != ZoneType.Start)
            return;

        var state = GetState(player);
        if (state.Running || state.Track != zone.Track)
            return;

        state.Running = true;
        state.StartedAt = DateTimeOffset.UtcNow;
        state.LastFinish = TimeSpan.Zero;
        state.LastStageSequence = 1;
        state.LastCheckpointSequence = 0;
        state.LastSplitLabel = "";
        state.LastSplitElapsed = TimeSpan.Zero;
        state.LastSplitAt = default;
        state.Track = zone.Track;
        SendPlayerOnlyTimerChat(player, zone.Track == 0
            ? "[gold][Timer][/] Started."
            : $"[gold][Timer][/] Bonus [white]{zone.Track}[/] started.");
    }

    private void HandleStageTouch(IPlayer player, PlayerTimerState state, RuntimeZone zone)
    {
        if (zone.Sequence <= 1 || state.LastStageSequence == zone.Sequence)
            return;

        state.LastStageSequence = zone.Sequence;
        var elapsed = DateTimeOffset.UtcNow - state.StartedAt;
        SetLastSplit(state, $"s{zone.Sequence}", elapsed);
        var splitComparison = SaveCheckpointRecord(GetRecordMapName(state.Track), player, zone.Sequence, elapsed, "stage");
        var prefix = zone.Track == 0 ? "Stage" : $"Bonus {zone.Track} Stage";
        SendPlayerOnlySplitChat(player, $"{prefix} [white]{zone.Sequence}[/]: [lime]{FormatTime(elapsed)}[/] {splitComparison}");
        RenderTimerHud(player, elapsed);
    }

    private void HandleCheckpointTouch(IPlayer player, PlayerTimerState state, RuntimeZone zone)
    {
        if (zone.Sequence <= 0 || state.LastCheckpointSequence == zone.Sequence)
            return;

        state.LastCheckpointSequence = zone.Sequence;
        var elapsed = DateTimeOffset.UtcNow - state.StartedAt;
        SetLastSplit(state, $"cp{zone.Sequence}", elapsed);
        var splitComparison = SaveCheckpointRecord(GetRecordMapName(state.Track), player, zone.Sequence, elapsed, "cp");
        var prefix = zone.Track == 0 ? "CP" : $"Bonus {zone.Track} CP";
        SendPlayerOnlySplitChat(player, $"{prefix} [white]{zone.Sequence}[/]: [lime]{FormatTime(elapsed)}[/] {splitComparison}");
        RenderTimerHud(player, elapsed);
    }

    private static void SendPlayerOnlySplitChat(IPlayer player, string message)
    {
        // Poor-SharpTimer-style split output: only the runner sees checkpoint/stage times.
        SendPlayerOnlyTimerChat(player, message);
    }

    private static void SendPlayerOnlyTimerChat(IPlayer player, string message)
    {
        player.SendChat(message.StartsWith("[gold][Timer][/]", StringComparison.Ordinal)
            ? message
            : $"[gold][Timer][/] {message}");
    }

    private static void SetLastSplit(PlayerTimerState state, string label, TimeSpan elapsed)
    {
        state.LastSplitLabel = label;
        state.LastSplitElapsed = elapsed;
        state.LastSplitAt = DateTimeOffset.UtcNow;
    }

    private void FinishRun(IPlayer player, PlayerTimerState state)
    {
        var elapsed = DateTimeOffset.UtcNow - state.StartedAt;
        state.LastFinish = elapsed;
        state.Running = false;
        RenderTimerHud(player, elapsed);

        var mapName = GetRecordMapName(state.Track);
        var previous = GetPlayerBest(mapName, player.SteamID);
        var isPb = previous == null || elapsed.TotalSeconds < previous.Seconds;

        if (isPb)
            SaveRecord(mapName, player, elapsed);

        var pbText = isPb ? " [lime]PB![/]" : "";
        var trackName = state.Track == 0 ? "" : $" bonus {state.Track}";
        Core.PlayerManager.SendChat($"[gold][Timer][/] [white]{player.Name}[/] finished{trackName} in [lime]{FormatTime(elapsed)}[/]{pbText}");
    }

    private void ResetPlayer(IPlayer player)
    {
        TeleportToStart(player, 0);
    }

    private void TeleportToStart(IPlayer player, int track)
    {
        EnsureZonesReady();
        var state = GetState(player);
        state.Reset();
        state.Track = track;
        RenderTimerHud(player, TimeSpan.Zero);

        var startZone = GetStartZone(track);
        if (startZone?.Origin == null)
        {
            var label = track == 0 ? "map_start" : $"bonus {track} start";
            player.SendChat($"[gold][Timer][/] Could not find [white]{label}[/]. Zone scan ran automatically; use [white]!zones[/] to inspect detected names.");
            return;
        }

        var origin = startZone.Origin.Value;
        var destination = new Vector(origin.X, origin.Y, origin.Z + 8.0f);
        player.Teleport(destination, startZone.Angles, new Vector(0.0f, 0.0f, 0.0f));
        player.SendChat(track == 0
            ? $"[gold][Timer][/] Restarted to [white]{startZone.TargetName}[/]."
            : $"[gold][Timer][/] Teleported to bonus [white]{track}[/] start [white]{startZone.TargetName}[/].");
    }

    private void TeleportToZone(IPlayer player, ZoneType type, int track, int sequence, string label)
    {
        EnsureZonesReady();
        var zone = _zones.Values
            .Where(candidate => candidate.Type == type && candidate.Track == track && candidate.Sequence == sequence)
            .OrderBy(candidate => candidate.EntityIndex)
            .FirstOrDefault();

        if (zone?.Origin == null)
        {
            player.SendChat($"[gold][Timer][/] Could not find [white]{label}[/]. Zone scan ran automatically; use [white]!zones[/] to inspect detected names.");
            return;
        }

        var state = GetState(player);
        state.Reset();
        state.Track = track;
        RenderTimerHud(player, TimeSpan.Zero);

        var origin = zone.Origin.Value;
        player.Teleport(new Vector(origin.X, origin.Y, origin.Z + 8.0f), zone.Angles, new Vector(0.0f, 0.0f, 0.0f));
        player.SendChat($"[gold][Timer][/] Teleported to [white]{zone.TargetName}[/].");
    }

    private PlayerTimerState GetState(IPlayer player)
    {
        var id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return new PlayerTimerState();

        return _players[id] ??= new PlayerTimerState();
    }

    private PlayerRecord? GetPlayerBest(string mapName, ulong steamId)
    {
        if (!_records.Maps.TryGetValue(mapName, out var records))
            return null;

        return records
            .Where(r => r.SteamId == steamId)
            .OrderBy(r => r.Seconds)
            .FirstOrDefault();
    }

    private int GetPlayerRank(string mapName, ulong steamId)
    {
        if (!_records.Maps.TryGetValue(mapName, out var records) || records.Count == 0)
            return 1;

        var ordered = records.OrderBy(r => r.Seconds).ToList();
        var index = ordered.FindIndex(r => r.SteamId == steamId);
        return index >= 0 ? index + 1 : ordered.Count + 1;
    }

    private int GetRecordCount(string mapName)
    {
        return _records.Maps.TryGetValue(mapName, out var records) ? records.Count : 0;
    }

    private CheckpointRecord? GetPlayerCheckpointBest(string mapName, int checkpoint, ulong steamId, string kind = "cp")
    {
        if (!_records.Checkpoints.TryGetValue(mapName, out var records))
            return null;

        return records
            .Where(r => GetCheckpointKind(r) == kind && r.Checkpoint == checkpoint && r.SteamId == steamId)
            .OrderBy(r => r.Seconds)
            .FirstOrDefault();
    }

    private CheckpointRecord? GetServerCheckpointBest(string mapName, int checkpoint, string kind = "cp")
    {
        if (!_records.Checkpoints.TryGetValue(mapName, out var records))
            return null;

        return records
            .Where(r => GetCheckpointKind(r) == kind && r.Checkpoint == checkpoint)
            .OrderBy(r => r.Seconds)
            .FirstOrDefault();
    }

    private static string GetCheckpointKind(CheckpointRecord record)
        => string.IsNullOrWhiteSpace(record.Kind) ? "cp" : record.Kind;

    private string GetMapTierText()
    {
        if (!_config.ShowMapTier)
            return "";

        var mapName = NormalizeWorkshopMapName(GetCurrentMapName());
        if (!_mapMetadata.TryGetValue(mapName, out var metadata) || metadata.Tier <= 0)
            return "";

        return $"t{metadata.Tier}";
    }

    private static string NormalizeWorkshopMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return "";

        mapName = mapName.Replace('\\', '/');
        var slash = mapName.LastIndexOf('/');
        if (slash >= 0)
            mapName = mapName[(slash + 1)..];

        if (mapName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            mapName = mapName[..^4];

        return mapName;
    }

    private void SendZonesInfo(IPlayer player)
    {
        ScanExistingZones();

        var starts = _zones.Values
            .Where(zone => zone.Type == ZoneType.Start)
            .OrderBy(zone => zone.Track)
            .ThenBy(zone => zone.TargetName)
            .Select(zone => $"{(zone.Track == 0 ? "main" : $"b{zone.Track}")}:{zone.TargetName}")
            .Take(12)
            .ToArray();

        var ends = _zones.Values
            .Where(zone => zone.Type == ZoneType.End)
            .OrderBy(zone => zone.Track)
            .ThenBy(zone => zone.TargetName)
            .Select(zone => $"{(zone.Track == 0 ? "main" : $"b{zone.Track}")}:{zone.TargetName}")
            .Take(12)
            .ToArray();

        var stages = _zones.Values
            .Where(zone => zone.Type == ZoneType.Stage)
            .OrderBy(zone => zone.Sequence)
            .Select(zone => $"s{zone.Sequence}:{zone.TargetName}")
            .Take(12)
            .ToArray();

        var checkpoints = _zones.Values
            .Where(zone => zone.Type == ZoneType.Checkpoint)
            .OrderBy(zone => zone.Track)
            .ThenBy(zone => zone.Sequence)
            .Select(zone => $"{(zone.Track == 0 ? "main" : $"b{zone.Track}")}:cp{zone.Sequence}:{zone.TargetName}")
            .Take(12)
            .ToArray();

        var unmatched = GetUnmatchedTriggerNames()
            .Take(12)
            .ToArray();

        player.SendChat($"[gold][Timer][/] Zones registered: [lime]{_zones.Count}[/]");
        player.SendChat($"[gold][Timer][/] Starts: [white]{(starts.Length == 0 ? "none" : string.Join(", ", starts))}[/]");
        player.SendChat($"[gold][Timer][/] Ends: [white]{(ends.Length == 0 ? "none" : string.Join(", ", ends))}[/]");
        player.SendChat($"[gold][Timer][/] Stages: [white]{(stages.Length == 0 ? "none" : string.Join(", ", stages))}[/]");
        player.SendChat($"[gold][Timer][/] CPs: [white]{(checkpoints.Length == 0 ? "none" : string.Join(", ", checkpoints))}[/]");
        if (unmatched.Length > 0)
            player.SendChat($"[gold][Timer][/] Unmatched triggers: [white]{string.Join(", ", unmatched)}[/]");
    }

    private IEnumerable<string> GetUnmatchedTriggerNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in Core.EntitySystem.GetAllEntitiesByDesignerName<CBaseEntity>("trigger_multiple"))
        {
            var name = entity.Entity?.Name ?? "";
            if (string.IsNullOrWhiteSpace(name) || _zones.ContainsKey(entity.Index))
                continue;

            names.Add(name);
        }

        return names;
    }

    private static int GetHorizontalSpeed(IPlayer player)
    {
        if (player.PlayerPawn?.IsValid != true)
            return 0;

        var velocity = player.PlayerPawn.AbsVelocity;
        return (int)Math.Clamp(Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y), 0.0, 9999.0);
    }

    private void SaveRecord(string mapName, IPlayer player, TimeSpan elapsed)
    {
        if (!_records.Maps.TryGetValue(mapName, out var records))
            _records.Maps[mapName] = records = new List<PlayerRecord>();

        records.RemoveAll(r => r.SteamId == player.SteamID);
        records.Add(new PlayerRecord
        {
            SteamId = player.SteamID,
            Name = player.Name,
            Seconds = elapsed.TotalSeconds,
            FinishedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        });

        _records.Maps[mapName] = records.OrderBy(r => r.Seconds).ToList();
        SaveRecords();
    }

    private string SaveCheckpointRecord(string mapName, IPlayer player, int checkpoint, TimeSpan elapsed, string kind)
    {
        var previousPlayerBest = GetPlayerCheckpointBest(mapName, checkpoint, player.SteamID, kind);
        var previousServerBest = GetServerCheckpointBest(mapName, checkpoint, kind);

        if (!_records.Checkpoints.TryGetValue(mapName, out var records))
            _records.Checkpoints[mapName] = records = new List<CheckpointRecord>();

        if (previousPlayerBest == null || elapsed.TotalSeconds < previousPlayerBest.Seconds)
        {
            records.RemoveAll(r => GetCheckpointKind(r) == kind && r.Checkpoint == checkpoint && r.SteamId == player.SteamID);
            records.Add(new CheckpointRecord
            {
                SteamId = player.SteamID,
                Name = player.Name,
                Kind = kind,
                Checkpoint = checkpoint,
                Seconds = elapsed.TotalSeconds,
                FinishedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            });

            _records.Checkpoints[mapName] = records
                .OrderBy(r => GetCheckpointKind(r))
                .ThenBy(r => r.Checkpoint)
                .ThenBy(r => r.Seconds)
                .ToList();
            SaveRecords();
        }

        var pbText = previousPlayerBest == null
            ? "[white]PB:first[/]"
            : $"[white]PB:{FormatTimeDiff(elapsed.TotalSeconds - previousPlayerBest.Seconds)}[/]";

        var srText = previousServerBest == null
            ? "[white]SR:first[/]"
            : $"[white]SR:{FormatTimeDiff(elapsed.TotalSeconds - previousServerBest.Seconds)}[/]";

        return $"{pbText} {srText}";
    }

    private void SendTopCheckpoint(IPlayer player, int checkpoint)
    {
        var state = GetState(player);
        var mapName = GetRecordMapName(state.Track);

        if (!_records.Checkpoints.TryGetValue(mapName, out var records))
        {
            player.SendChat($"[gold][Timer][/] No checkpoint [white]{checkpoint}[/] records on [white]{mapName}[/] yet.");
            return;
        }

        var top = records
            .Where(r => GetCheckpointKind(r) == "cp" && r.Checkpoint == checkpoint)
            .OrderBy(r => r.Seconds)
            .Take(10)
            .ToList();

        if (top.Count == 0)
        {
            player.SendChat($"[gold][Timer][/] No checkpoint [white]{checkpoint}[/] records on [white]{mapName}[/] yet.");
            return;
        }

        player.SendChat($"[gold][Timer][/] Top CP [white]{checkpoint}[/] for [white]{mapName}[/]:");
        for (var i = 0; i < top.Count; i++)
            player.SendChat($"[grey]{i + 1}.[/] [lime]{FormatTime(TimeSpan.FromSeconds(top[i].Seconds))}[/] [white]{top[i].Name}[/]");
    }

    private void ResetAllPlayers()
    {
        for (var i = 0; i < _players.Length; i++)
            _players[i]?.Reset();
    }

    private RuntimeZone? GetStartZone(int track)
    {
        return _zones.Values
            .Where(zone => zone.Type == ZoneType.Start && zone.Track == track)
            .OrderBy(zone => zone.TargetName.Equals(track == 0 ? "map_start" : $"bonus{track}_start", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(zone => zone.EntityIndex)
            .FirstOrDefault();
    }

    private string GetCurrentMapName() => Core.Engine.GlobalVars.MapName.Value;

    private string GetRecordMapName(int track)
    {
        var mapName = GetCurrentMapName();
        return track == 0 ? mapName : $"{mapName}_bonus{track}";
    }

    private string ConfigRoot => Path.Combine(Path.GetFullPath(Path.Combine(Core.PluginPath, "..", "..")), "configs", "plugins", "SurfTimer");
    private string DataRoot => Path.Combine(Path.GetFullPath(Path.Combine(Core.PluginPath, "..", "..")), "data", "plugins", "SurfTimer");
    private string ConfigPath => Path.Combine(ConfigRoot, "config.jsonc");
    private string RecordsPath => Path.Combine(DataRoot, "records.json");
    private string MapMetadataPath => Path.Combine(Core.PluginPath, "resources", "mapdata", "surf_.json");

    private void LoadConfig()
    {
        Directory.CreateDirectory(ConfigRoot);
        if (!File.Exists(ConfigPath))
            File.WriteAllText(ConfigPath, GenerateDefaultConfig());

        try
        {
            _config = JsonSerializer.Deserialize<SurfTimerConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new SurfTimerConfig();
            _config.HudUpdateEveryTicks = 1;
            _config.ParticleHudShowLabel = false;
            _config.StartZoneNames = MergeDefaults(_config.StartZoneNames, new SurfTimerConfig().StartZoneNames);
            _config.EndZoneNames = MergeDefaults(_config.EndZoneNames, new SurfTimerConfig().EndZoneNames);
            if (Math.Abs(_config.TimerHudSpeedLabelOffsetX) < 0.001f && Math.Abs(_config.TimerHudSpeedLabelOffsetY) < 0.001f)
            {
                _config.TimerHudSpeedLabelOffsetX = _config.TimerHudSpeedOffsetX;
                _config.TimerHudSpeedLabelOffsetY = _config.TimerHudSpeedOffsetY;
            }
            if (Math.Abs(_config.TimerHudSpeedLabelScaleMultiplier) < 0.001f)
                _config.TimerHudSpeedLabelScaleMultiplier = 1.0f;
            if (Math.Abs(_config.TimerHudSpeedLabelSpacingMultiplier) < 0.001f)
                _config.TimerHudSpeedLabelSpacingMultiplier = 1.0f;
            if ((Math.Abs(_config.TimerHudDetailOffsetX - 2.4f) < 0.001f && Math.Abs(_config.TimerHudDetailOffsetY - 2.7f) < 0.001f) ||
                (Math.Abs(_config.TimerHudDetailOffsetX - 3.6f) < 0.001f && Math.Abs(_config.TimerHudDetailOffsetY - 0.5f) < 0.001f))
            {
                _config.TimerHudDetailOffsetX = -1.1f;
                _config.TimerHudDetailOffsetY = -0.3f;
                _config.TimerHudDetailScaleMultiplier = 0.7f;
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Failed to load config: {Message}", ex.Message);
            _config = new SurfTimerConfig();
        }
    }

    private void LoadRecords()
    {
        Directory.CreateDirectory(DataRoot);
        if (!File.Exists(RecordsPath))
        {
            _records = new RecordsFile();
            return;
        }

        try
        {
            _records = JsonSerializer.Deserialize<RecordsFile>(File.ReadAllText(RecordsPath), JsonOptions) ?? new RecordsFile();
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Failed to load records: {Message}", ex.Message);
            _records = new RecordsFile();
        }
    }

    private void LoadMapMetadata()
    {
        if (!File.Exists(MapMetadataPath))
        {
            Core.Logger.LogWarning("[SurfTimer] Map tier metadata not found at {Path}", MapMetadataPath);
            _mapMetadata = new Dictionary<string, MapMetadata>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            _mapMetadata = JsonSerializer.Deserialize<Dictionary<string, MapMetadata>>(File.ReadAllText(MapMetadataPath), JsonOptions)
                ?? new Dictionary<string, MapMetadata>(StringComparer.OrdinalIgnoreCase);

            _mapMetadata = new Dictionary<string, MapMetadata>(_mapMetadata, StringComparer.OrdinalIgnoreCase);
            Core.Logger.LogWarning("[SurfTimer] Loaded map tier metadata for {Count} maps.", _mapMetadata.Count);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Failed to load map tier metadata: {Message}", ex.Message);
            _mapMetadata = new Dictionary<string, MapMetadata>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveRecords()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            File.WriteAllText(RecordsPath, JsonSerializer.Serialize(_records, JsonOptions));
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Failed to save records: {Message}", ex.Message);
        }
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigRoot);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, JsonOptions));
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Failed to save config: {Message}", ex.Message);
        }
    }

    private static void ApplyDefaultTimerHudPreset(SurfTimerConfig config)
    {
        config.TimerHudBaseX = -5.8f;
        config.TimerHudBaseY = -12.9f;
        config.TimerHudTimeOffsetX = 0.6f;
        config.TimerHudTimeOffsetY = -4.6f;
        config.TimerHudSpeedOffsetX = 0.0f;
        config.TimerHudSpeedOffsetY = -3.25f;
        config.TimerHudSpeedLabelOffsetX = -2.2f;
        config.TimerHudSpeedLabelOffsetY = -10.221f;
        config.TimerHudDetailOffsetX = -3.8f;
        config.TimerHudDetailOffsetY = -8.471f;
        config.TimerHudSpacing = 1.25f;
        config.TimerHudScale = 0.024f;
        config.TimerHudSpeedLabelScaleMultiplier = 0.7f;
        config.TimerHudSpeedLabelSpacingMultiplier = 1.0f;
        config.TimerHudDetailScaleMultiplier = 0.7f;
        config.TimerHudTimeRed = 156;
        config.TimerHudTimeGreen = 255;
        config.TimerHudTimeBlue = 87;
        config.TimerHudSpeedLabelRed = 156;
        config.TimerHudSpeedLabelGreen = 255;
        config.TimerHudSpeedLabelBlue = 87;
        config.TimerHudSpeedRed = 226;
        config.TimerHudSpeedGreen = 67;
        config.TimerHudSpeedBlue = 255;
        config.TimerHudDetailRed = 156;
        config.TimerHudDetailGreen = 255;
        config.TimerHudDetailBlue = 87;
    }

    private void LogDebug(string message, params object?[] args)
    {
        if (_config.Debug)
            Core.Logger.LogWarning(message, args);
    }

    private static string[] MergeDefaults(string[]? configured, string[] defaults)
    {
        return (configured ?? [])
            .Concat(defaults)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";

    private static string FormatTimeDiff(double seconds)
    {
        var color = seconds <= 0 ? "lime" : "red";
        var sign = seconds <= 0 ? "-" : "+";
        var duration = TimeSpan.FromSeconds(Math.Abs(seconds));
        return $"[{color}]{sign}{FormatTime(duration)}[/]";
    }

    private static string GenerateDefaultConfig() =>
        """
        {
          "ConfigVersion": "0.1.0",
          "Debug": false,
          "EnableHud": true,
          "PreferParticleHud": true,
          "ShowMapTier": true,
          "HudUpdateEveryTicks": 1,
          "HudDurationMs": 1000,
          "ParticleHudCharacters": 9,
          "ParticleHudXStart": -4.0,
          "ParticleHudSpacing": 1.0,
          "ParticleHudYOffset": -2.0,
          "ParticleHudScale": 0.04,
          "ParticleHudShowLabel": false,
          "ParticleHudLabelText": "timer",
          "ParticleHudLabelXStart": -2.0,
          "ParticleHudLabelSpacing": 1.0,
          "ParticleHudLabelYOffset": -2.7,
          "ParticleHudLabelScale": 0.032,
          "TimerHudBaseX": -5.8,
          "TimerHudBaseY": -12.9,
          "TimerHudSpacing": 1.25,
          "TimerHudScale": 0.024,
          "TimerHudTimeOffsetX": 0.6,
          "TimerHudTimeOffsetY": -4.6,
          "TimerHudSpeedOffsetX": 0.0,
          "TimerHudSpeedOffsetY": -3.25,
          "TimerHudSpeedLabelOffsetX": -2.2,
          "TimerHudSpeedLabelOffsetY": -10.221,
          "TimerHudSpeedLabelScaleMultiplier": 0.7,
          "TimerHudSpeedLabelSpacingMultiplier": 1.0,
          "TimerHudDetailOffsetX": -3.8,
          "TimerHudDetailOffsetY": -8.471,
          "TimerHudDetailScaleMultiplier": 0.7,
          "TimerHudTimeRed": 156,
          "TimerHudTimeGreen": 255,
          "TimerHudTimeBlue": 87,
          "TimerHudSpeedLabelRed": 156,
          "TimerHudSpeedLabelGreen": 255,
          "TimerHudSpeedLabelBlue": 87,
          "TimerHudSpeedRed": 226,
          "TimerHudSpeedGreen": 67,
          "TimerHudSpeedBlue": 255,
          "TimerHudDetailRed": 156,
          "TimerHudDetailGreen": 255,
          "TimerHudDetailBlue": 87,
          "AutoScanZones": true,
          "ZoneMarkerFontSize": 48.0,
          "ZoneMarkerHeightOffset": 96.0,
          "StartZoneNames": [ "map_start", "map_startzone", "s1_start", "stage1_start", "timer_start", "timer_startzone", "zone_start", "start" ],
          "EndZoneNames": [ "map_end", "map_endzone", "s1_end", "stage1_end", "timer_end", "timer_endzone", "zone_end", "end" ]
        }
        """;

    private void RenderTimerHud(IPlayer player, TimeSpan elapsed)
    {
        if (_config.PreferParticleHud && _particleHud != null && _particleHudSetText != null && _particleHudRenderFailureCooldownTicks <= 0)
        {
            try
            {
                RenderParticleTimerLayout(player, elapsed);
                return;
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning("[SurfTimer] Particle HUD render failed: {Message}", ex.InnerException?.Message ?? ex.Message);
                _particleHudRenderFailureCooldownTicks = 64;
            }
        }

        if (_config.PreferParticleHud && _particleHud != null)
            return;

        player.SendCenterHTML($"<font color='#9cff57' class='fontSize-l'>{FormatTime(elapsed)}</font>", _config.HudDurationMs);
    }

    private void RenderParticleTimerLayout(IPlayer player, TimeSpan elapsed)
    {
        var state = GetState(player);
        var mapName = GetRecordMapName(state.Track);
        var speed = GetHorizontalSpeed(player);
        var best = GetPlayerBest(mapName, player.SteamID);
        var rank = GetPlayerRank(mapName, player.SteamID);
        var total = Math.Max(GetRecordCount(mapName), 1);
        var pbText = best == null ? FormatTime(TimeSpan.Zero) : FormatTime(TimeSpan.FromSeconds(best.Seconds));
        var tierText = GetMapTierText();
        var splitText = GetRecentSplitText(state);
        var detail = !string.IsNullOrEmpty(splitText)
            ? splitText
            : string.IsNullOrEmpty(tierText)
            ? $"rank:{rank}/{total} pb:{pbText}"
            : $"rank:{rank}/{total} {tierText} pb:{pbText}";
        var spacing = _config.TimerHudSpacing;
        var scale = _config.TimerHudScale;
        var topX = _config.TimerHudBaseX + _config.TimerHudTimeOffsetX;
        var topY = _config.TimerHudBaseY + _config.TimerHudTimeOffsetY;
        var speedX = _config.TimerHudBaseX + _config.TimerHudSpeedOffsetX;
        var speedY = _config.TimerHudBaseY + _config.TimerHudSpeedOffsetY;
        var speedLabelX = _config.TimerHudBaseX + _config.TimerHudSpeedLabelOffsetX;
        var speedLabelY = _config.TimerHudBaseY + _config.TimerHudSpeedLabelOffsetY;
        var detailX = _config.TimerHudBaseX + _config.TimerHudDetailOffsetX;
        var detailY = _config.TimerHudBaseY + _config.TimerHudDetailOffsetY;
        var speedLabelScaleMultiplier = Math.Clamp(_config.TimerHudSpeedLabelScaleMultiplier, 0.25f, 1.5f);
        var speedLabelSpacingMultiplier = Math.Clamp(_config.TimerHudSpeedLabelSpacingMultiplier, 0.25f, 1.5f);
        var speedLabelScale = scale * speedLabelScaleMultiplier;
        var speedLabelSpacing = spacing * speedLabelSpacingMultiplier;
        var detailScaleMultiplier = Math.Clamp(_config.TimerHudDetailScaleMultiplier, 0.25f, 1.5f);
        var detailScale = scale * detailScaleMultiplier;
        var detailSpacing = spacing * detailScaleMultiplier;

        if (_particleHudSetGlyphs != null)
        {
            SetParticleGlyphs(player, "surftimer-widget", builder =>
            {
                builder.AddLine(FormatTime(elapsed), 9, _config.TimerHudTimeOffsetX, spacing, _config.TimerHudTimeOffsetY, scale, _config.TimerHudTimeRed, _config.TimerHudTimeGreen, _config.TimerHudTimeBlue);
                builder.AddLine("speed:", 6, _config.TimerHudSpeedLabelOffsetX, speedLabelSpacing, _config.TimerHudSpeedLabelOffsetY, speedLabelScale, _config.TimerHudSpeedLabelRed, _config.TimerHudSpeedLabelGreen, _config.TimerHudSpeedLabelBlue);
                builder.AddLine(speed.ToString("0000"), 4, _config.TimerHudSpeedOffsetX + spacing * 6.0f, spacing, _config.TimerHudSpeedOffsetY, scale, _config.TimerHudSpeedRed, _config.TimerHudSpeedGreen, _config.TimerHudSpeedBlue);
                builder.AddLine(detail, Math.Min(detail.Length, 28), _config.TimerHudDetailOffsetX, detailSpacing, _config.TimerHudDetailOffsetY, detailScale, _config.TimerHudDetailRed, _config.TimerHudDetailGreen, _config.TimerHudDetailBlue);
            }, _config.TimerHudBaseX, _config.TimerHudBaseY);
            return;
        }

        SetParticleText(player, "surftimer-time", FormatTime(elapsed), 9, topX, spacing, topY, scale, _config.TimerHudTimeRed, _config.TimerHudTimeGreen, _config.TimerHudTimeBlue);
        SetParticleText(player, "surftimer-speed-label", "speed:", 6, speedLabelX, speedLabelSpacing, speedLabelY, speedLabelScale, _config.TimerHudSpeedLabelRed, _config.TimerHudSpeedLabelGreen, _config.TimerHudSpeedLabelBlue);
        SetParticleText(player, "surftimer-speed", speed.ToString("0000"), 4, speedX + (spacing * 6.0f), spacing, speedY, scale, _config.TimerHudSpeedRed, _config.TimerHudSpeedGreen, _config.TimerHudSpeedBlue);
        SetParticleText(player, "surftimer-rank", detail, Math.Min(detail.Length, 28), detailX, detailSpacing, detailY, detailScale, _config.TimerHudDetailRed, _config.TimerHudDetailGreen, _config.TimerHudDetailBlue);
    }

    private sealed class ParticleGlyphBuilder
    {
        public readonly List<char> Characters = [];
        public readonly List<float> XOffsets = [];
        public readonly List<float> YOffsets = [];
        public readonly List<float> Scales = [];
        public readonly List<int> Reds = [];
        public readonly List<int> Greens = [];
        public readonly List<int> Blues = [];

        public void AddLine(string text, int maxCharacters, float xStart, float spacing, float yOffset, float scale, int red, int green, int blue)
        {
            text ??= "";
            maxCharacters = Math.Clamp(maxCharacters, 0, text.Length);
            for (var i = 0; i < maxCharacters; i++)
            {
                Characters.Add(text[i]);
                XOffsets.Add(xStart + i * spacing);
                YOffsets.Add(yOffset);
                Scales.Add(scale);
                Reds.Add(red);
                Greens.Add(green);
                Blues.Add(blue);
            }
        }
    }

    private void SetParticleGlyphs(IPlayer player, string channel, Action<ParticleGlyphBuilder> build, float baseX, float baseY)
    {
        if (_particleHud == null || _particleHudSetGlyphs == null)
            return;

        var builder = new ParticleGlyphBuilder();
        build(builder);
        var maxCharacters = 64;
        _particleHudSetGlyphs.Invoke(_particleHud,
        [
            player,
            channel,
            new string(builder.Characters.ToArray()),
            maxCharacters,
            baseX,
            baseY,
            builder.XOffsets.ToArray(),
            builder.YOffsets.ToArray(),
            builder.Scales.ToArray(),
            builder.Reds.ToArray(),
            builder.Greens.ToArray(),
            builder.Blues.ToArray(),
        ]);
    }

    private static string GetRecentSplitText(PlayerTimerState state)
    {
        if (string.IsNullOrEmpty(state.LastSplitLabel) || state.LastSplitAt == default)
            return "";

        return DateTimeOffset.UtcNow - state.LastSplitAt <= TimeSpan.FromSeconds(4)
            ? $"{state.LastSplitLabel}:{FormatTime(state.LastSplitElapsed)}"
            : "";
    }

    private void SetParticleText(IPlayer player, string channel, string text, int maxCharacters, float xStart, float spacing, float yOffset, float scale, int red, int green, int blue)
    {
        _particleHudSetText!.Invoke(_particleHud,
        [
            player,
            channel,
            text,
            maxCharacters,
            xStart,
            spacing,
            yOffset,
            scale,
            red,
            green,
            blue,
        ]);
    }

    private void ClearTimerHud(IPlayer player)
    {
        if (_particleHud == null || _particleHudClear == null)
            return;

        try
        {
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-main"]);
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-label"]);
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-hash"]);
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-widget"]);
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-time"]);
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-speed-label"]);
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-speed"]);
            _particleHudClear.Invoke(_particleHud, [player, "surftimer-rank"]);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Particle HUD clear failed: {Message}", ex.InnerException?.Message ?? ex.Message);
        }
    }

    private void ClearAllTimerHuds()
    {
        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (player?.IsValid == true && !player.IsFakeClient)
                ClearTimerHud(player);
        }
    }

    private void ScheduleTimerHudRebuild()
    {
        if (!_config.EnableHud)
            return;

        Core.Scheduler.NextTick(RebuildAllTimerHuds);
        Core.Scheduler.DelayBySeconds(0.25f, RebuildAllTimerHuds);
        Core.Scheduler.DelayBySeconds(1.0f, RebuildAllTimerHuds);
        Core.Scheduler.DelayBySeconds(2.0f, RebuildAllTimerHuds);
    }

    private void ScheduleTimerHudRebuild(IPlayer player)
    {
        if (!_config.EnableHud)
            return;

        Core.Scheduler.NextTick(() => RebuildTimerHud(player));
        Core.Scheduler.DelayBySeconds(0.1f, () => RebuildTimerHud(player));
        Core.Scheduler.DelayBySeconds(0.25f, () => RebuildTimerHud(player));
        Core.Scheduler.DelayBySeconds(0.5f, () => RebuildTimerHud(player));
        Core.Scheduler.DelayBySeconds(1.5f, () => RebuildTimerHud(player));
        Core.Scheduler.DelayBySeconds(3.0f, () => RebuildTimerHud(player));
    }

    private void RebuildAllTimerHuds()
    {
        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (player?.IsValid == true && !player.IsFakeClient)
                RebuildTimerHud(player);
        }
    }

    private void RebuildTimerHud(IPlayer player)
    {
        if (player?.IsValid != true || player.IsFakeClient)
            return;

        ClearTimerHud(player);
        var state = GetState(player);
        RenderTimerHud(player, state.Running ? DateTimeOffset.UtcNow - state.StartedAt : state.LastFinish);
    }

    private void SpawnZoneMarkers()
    {
        ClearZoneMarkers();

        foreach (var zone in _zones.Values
            .Where(zone => zone.Type is ZoneType.Start or ZoneType.End or ZoneType.Checkpoint)
            .OrderBy(zone => zone.Track)
            .ThenBy(zone => zone.Type)
            .ThenBy(zone => zone.Sequence)
            .Take(64))
        {
            SpawnZoneMarker(zone);
        }
    }

    private void SpawnZoneMarker(RuntimeZone zone)
    {
        if (zone.Origin == null)
            return;

        try
        {
            var origin = zone.Origin.Value;
            var topOffset = GetZoneTopOffset(zone);
            var markerOrigin = new Vector(origin.X, origin.Y, origin.Z + topOffset + _config.ZoneMarkerHeightOffset);
            using var kv = new CEntityKeyValues();
            kv.SetVector("origin", markerOrigin);

            var marker = Core.EntitySystem.CreateEntityByDesignerName<CPointWorldText>("point_worldtext");
            marker.MessageText = GetZoneMarkerText(zone);
            marker.FontName = "Stratum2";
            marker.Enabled = true;
            marker.Fullbright = true;
            marker.FontSize = _config.ZoneMarkerFontSize;
            marker.WorldUnitsPerPx = 0.25f;
            marker.DrawBackground = true;
            marker.Color = GetZoneMarkerColor(zone);
            marker.DispatchSpawn(kv);
            _zoneMarkers.Add(marker);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[SurfTimer] Failed to spawn zone marker for {Name}: {Message}", zone.TargetName, ex.Message);
        }
    }

    private void ClearZoneMarkers()
    {
        foreach (var marker in _zoneMarkers)
        {
            try
            {
                if (marker.IsValidEntity)
                    marker.AcceptInput("Kill", "");
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        _zoneMarkers.Clear();
    }

    private static float GetZoneTopOffset(RuntimeZone zone)
    {
        if (zone.Maxs == null)
            return 0.0f;

        return Math.Max(zone.Maxs.Value.Z, 0.0f);
    }

    private static string GetZoneMarkerText(RuntimeZone zone)
    {
        return zone.Type switch
        {
            ZoneType.Start when zone.Track == 0 => "START",
            ZoneType.Start => $"BONUS {zone.Track} START",
            ZoneType.End when zone.Track == 0 => "END",
            ZoneType.End => $"BONUS {zone.Track} END",
            ZoneType.Checkpoint when zone.Track == 0 => $"CP {zone.Sequence}",
            ZoneType.Checkpoint => $"B{zone.Track} CP {zone.Sequence}",
            _ => zone.TargetName.ToUpperInvariant(),
        };
    }

    private static Color GetZoneMarkerColor(RuntimeZone zone)
    {
        return zone.Type switch
        {
            ZoneType.Start => new Color(80, 255, 120, 255),
            ZoneType.End => new Color(255, 80, 80, 255),
            ZoneType.Checkpoint => new Color(80, 180, 255, 255),
            _ => new Color(255, 255, 255, 255),
        };
    }

    private void OpenTimerHudMenu(IPlayer player)
    {
        Core.MenusAPI.OpenMenuForPlayer(player, BuildTimerHudMenu(player));
    }

    private IMenuAPI BuildTimerHudMenu(IPlayer player)
    {
        var builder = Core.MenusAPI.CreateBuilder();
        builder.Design.SetMenuTitle(BuildTimerHudTitle());
        builder.SetPlayerFrozen(false);
        builder.SetMoveForwardButton(KeyBind.Mouse1);
        builder.SetMoveBackwardButton(KeyBind.Mouse2);

        void AddButton(string label, int optionIndex, Action change)
        {
        var button = new ButtonMenuOption(label);
            button.Click += (_, _) =>
            {
                Core.Scheduler.NextTick(() =>
                {
                    change();
                    SaveConfig();
                    var state = GetState(player);
                    RenderTimerHud(player, state.Running ? DateTimeOffset.UtcNow - state.StartedAt : state.LastFinish);
                    var menu = BuildTimerHudMenu(player);
                    Core.MenusAPI.OpenMenuForPlayer(player, menu);
                    menu.MoveToOptionIndex(player, optionIndex);
                });
                return ValueTask.CompletedTask;
            };
            builder.AddOption(button);
        }

        AddButton("<font color='#ADD8E6'>Widget</font> ◄", 0, () => MoveTimerHudWidget(-0.2f, 0.0f));
        AddButton("<font color='#ADD8E6'>Widget</font> ►", 1, () => MoveTimerHudWidget(0.2f, 0.0f));
        AddButton("<font color='#ADD8E6'>Widget</font> ▼", 2, () => MoveTimerHudWidget(0.0f, 0.2f));
        AddButton("<font color='#ADD8E6'>Widget</font> ▲", 3, () => MoveTimerHudWidget(0.0f, -0.2f));
        AddButton("<font color='#00FF00'>[+] Scale</font>", 4, () => _config.TimerHudScale = Math.Clamp(_config.TimerHudScale + 0.002f, 0.004f, 0.2f));
        AddButton("<font color='#FF4444'>[-] Scale</font>", 5, () => _config.TimerHudScale = Math.Clamp(_config.TimerHudScale - 0.002f, 0.004f, 0.2f));
        AddButton("<font color='#00FF00'>[+] Spacing</font>", 6, () => _config.TimerHudSpacing = Math.Clamp(_config.TimerHudSpacing + 0.05f, 0.1f, 3.0f));
        AddButton("<font color='#FF4444'>[-] Spacing</font>", 7, () => _config.TimerHudSpacing = Math.Clamp(_config.TimerHudSpacing - 0.05f, 0.1f, 3.0f));
        AddButton("<font color='#BBBBBB'>Rank</font> ◄", 8, () => _config.TimerHudDetailOffsetX -= 0.1f);
        AddButton("<font color='#BBBBBB'>Rank</font> ►", 9, () => _config.TimerHudDetailOffsetX += 0.1f);
        AddButton("<font color='#BBBBBB'>Rank</font> ▼", 10, () => _config.TimerHudDetailOffsetY += 0.1f);
        AddButton("<font color='#BBBBBB'>Rank</font> ▲", 11, () => _config.TimerHudDetailOffsetY -= 0.1f);
        AddButton("<font color='#BBBBBB'>Rank</font> [+] Size", 12, () => _config.TimerHudDetailScaleMultiplier = Math.Clamp(_config.TimerHudDetailScaleMultiplier + 0.05f, 0.25f, 1.5f));
        AddButton("<font color='#BBBBBB'>Rank</font> [-] Size", 13, () => _config.TimerHudDetailScaleMultiplier = Math.Clamp(_config.TimerHudDetailScaleMultiplier - 0.05f, 0.25f, 1.5f));
        AddButton("<font color='#50D7FF'>Speed Label</font> ◄", 14, () => _config.TimerHudSpeedLabelOffsetX -= 0.1f);
        AddButton("<font color='#50D7FF'>Speed Label</font> ►", 15, () => _config.TimerHudSpeedLabelOffsetX += 0.1f);
        AddButton("<font color='#50D7FF'>Speed Label</font> ▼", 16, () => _config.TimerHudSpeedLabelOffsetY += 0.1f);
        AddButton("<font color='#50D7FF'>Speed Label</font> ▲", 17, () => _config.TimerHudSpeedLabelOffsetY -= 0.1f);
        AddButton("<font color='#50D7FF'>Speed Label</font> [+] Size", 18, () => _config.TimerHudSpeedLabelScaleMultiplier = Math.Clamp(_config.TimerHudSpeedLabelScaleMultiplier + 0.05f, 0.25f, 1.5f));
        AddButton("<font color='#50D7FF'>Speed Label</font> [-] Size", 19, () => _config.TimerHudSpeedLabelScaleMultiplier = Math.Clamp(_config.TimerHudSpeedLabelScaleMultiplier - 0.05f, 0.25f, 1.5f));
        AddButton("<font color='#50D7FF'>Speed Label</font> [+] Spacing", 20, () => _config.TimerHudSpeedLabelSpacingMultiplier = Math.Clamp(_config.TimerHudSpeedLabelSpacingMultiplier + 0.05f, 0.25f, 1.5f));
        AddButton("<font color='#50D7FF'>Speed Label</font> [-] Spacing", 21, () => _config.TimerHudSpeedLabelSpacingMultiplier = Math.Clamp(_config.TimerHudSpeedLabelSpacingMultiplier - 0.05f, 0.25f, 1.5f));
        AddButton("<font color='#E243FF'>Cycle Time Colour</font>", 22, () => CycleHudColor("time"));
        AddButton("<font color='#50D7FF'>Cycle Label Colour</font>", 23, () => CycleHudColor("label"));
        AddButton("<font color='#E243FF'>Cycle Speed Colour</font>", 24, () => CycleHudColor("speed"));
        AddButton("<font color='#BBBBBB'>Cycle Rank Colour</font>", 25, () => CycleHudColor("rank"));

        AddButton("<font color='#FFD700'>Reset Default</font>", 26, () => ApplyDefaultTimerHudPreset(_config));

        var info = new ButtonMenuOption("<font color='#FFD700'>Print Values</font>");
        info.Click += (_, _) =>
        {
            Core.Scheduler.NextTick(() => SendTimerHudInfo(player));
            return ValueTask.CompletedTask;
        };
        builder.AddOption(info);

        return builder.Build();
    }

    private string BuildTimerHudTitle()
    {
        return $"<font color='#FFD700'>Timer HUD</font> X:<font color='#ADD8E6'>{_config.TimerHudBaseX:+0.00;-0.00;0.00}</font> Y:<font color='#ADD8E6'>{_config.TimerHudBaseY:+0.00;-0.00;0.00}</font> S:<font color='#00FF00'>{_config.TimerHudScale:0.000}</font>";
    }

    private void MoveTimerHudWidget(float deltaX, float deltaY)
    {
        var speedLabelScaleMultiplier = Math.Clamp(_config.TimerHudSpeedLabelScaleMultiplier, 0.25f, 1.5f);
        var detailScaleMultiplier = Math.Clamp(_config.TimerHudDetailScaleMultiplier, 0.25f, 1.5f);

        _config.TimerHudTimeOffsetX += deltaX;
        _config.TimerHudTimeOffsetY += deltaY;
        _config.TimerHudSpeedOffsetX += deltaX;
        _config.TimerHudSpeedOffsetY += deltaY;
        _config.TimerHudSpeedLabelOffsetX += deltaX / speedLabelScaleMultiplier;
        _config.TimerHudSpeedLabelOffsetY += deltaY / speedLabelScaleMultiplier;
        _config.TimerHudDetailOffsetX += deltaX / detailScaleMultiplier;
        _config.TimerHudDetailOffsetY += deltaY / detailScaleMultiplier;
    }

    private void SendTimerHudInfo(IPlayer player)
    {
        player.SendChat("[gold][TimerHUD][/] Current widget layout:");
        player.SendChat($"[white]TimerHudBaseX[/] = [lime]{_config.TimerHudBaseX:F3}[/]  [white]TimerHudBaseY[/] = [lime]{_config.TimerHudBaseY:F3}[/]");
        player.SendChat($"[white]TimerHudTimeOffsetX[/] = [lime]{_config.TimerHudTimeOffsetX:F3}[/]  [white]TimerHudTimeOffsetY[/] = [lime]{_config.TimerHudTimeOffsetY:F3}[/]");
        player.SendChat($"[white]TimerHudSpeedOffsetX[/] = [lime]{_config.TimerHudSpeedOffsetX:F3}[/]  [white]TimerHudSpeedOffsetY[/] = [lime]{_config.TimerHudSpeedOffsetY:F3}[/]");
        player.SendChat($"[white]TimerHudSpeedLabelOffsetX[/] = [lime]{_config.TimerHudSpeedLabelOffsetX:F3}[/]  [white]TimerHudSpeedLabelOffsetY[/] = [lime]{_config.TimerHudSpeedLabelOffsetY:F3}[/]");
        player.SendChat($"[white]TimerHudDetailOffsetX[/] = [lime]{_config.TimerHudDetailOffsetX:F3}[/]  [white]TimerHudDetailOffsetY[/] = [lime]{_config.TimerHudDetailOffsetY:F3}[/]");
        player.SendChat($"[white]TimerHudSpacing[/] = [lime]{_config.TimerHudSpacing:F3}[/]");
        player.SendChat($"[white]TimerHudScale[/] = [lime]{_config.TimerHudScale:F4}[/]");
        player.SendChat($"[white]TimerHudSpeedLabelScaleMultiplier[/] = [lime]{_config.TimerHudSpeedLabelScaleMultiplier:F3}[/]");
        player.SendChat($"[white]TimerHudSpeedLabelSpacingMultiplier[/] = [lime]{_config.TimerHudSpeedLabelSpacingMultiplier:F3}[/]");
        player.SendChat($"[white]TimerHudDetailScaleMultiplier[/] = [lime]{_config.TimerHudDetailScaleMultiplier:F3}[/]");
        SendTimerHudColors(player);
    }

    private void SendTimerHudColors(IPlayer player)
    {
        player.SendChat("[gold][TimerHUD][/] Colours:");
        player.SendChat($"[white]time[/] = [lime]{_config.TimerHudTimeRed} {_config.TimerHudTimeGreen} {_config.TimerHudTimeBlue}[/]");
        player.SendChat($"[white]label[/] = [lime]{_config.TimerHudSpeedLabelRed} {_config.TimerHudSpeedLabelGreen} {_config.TimerHudSpeedLabelBlue}[/]");
        player.SendChat($"[white]speed[/] = [lime]{_config.TimerHudSpeedRed} {_config.TimerHudSpeedGreen} {_config.TimerHudSpeedBlue}[/]");
        player.SendChat($"[white]rank[/] = [lime]{_config.TimerHudDetailRed} {_config.TimerHudDetailGreen} {_config.TimerHudDetailBlue}[/]");
        player.SendChat("[gold][TimerHUD][/] Set with [white]!timerhud color time 226 67 255[/]");
    }

    private void HandleTimerHudColorCommand(IPlayer player, string[] args)
    {
        if (args.Length != 4 ||
            !int.TryParse(args[1], out var red) ||
            !int.TryParse(args[2], out var green) ||
            !int.TryParse(args[3], out var blue))
        {
            player.SendChat("[gold][TimerHUD][/] Usage: [white]!timerhud color <time|label|speed|rank> <r> <g> <b>[/]");
            return;
        }

        if (!SetHudColor(args[0], red, green, blue))
        {
            player.SendChat("[gold][TimerHUD][/] Unknown colour target. Use [white]time[/], [white]label[/], [white]speed[/], or [white]rank[/].");
            return;
        }

        SaveConfig();
        ClearTimerHud(player);
        var state = GetState(player);
        RenderTimerHud(player, state.Running ? DateTimeOffset.UtcNow - state.StartedAt : state.LastFinish);
        player.SendChat($"[gold][TimerHUD][/] Set [white]{args[0].ToLowerInvariant()}[/] colour to [lime]{ClampColor(red)} {ClampColor(green)} {ClampColor(blue)}[/].");
    }

    private void CycleHudColor(string target)
    {
        var current = GetHudColor(target);
        var palette = HudColorPalette;
        var index = Array.FindIndex(palette, color => color.Red == current.Red && color.Green == current.Green && color.Blue == current.Blue);
        var next = palette[(index + 1 + palette.Length) % palette.Length];
        SetHudColor(target, next.Red, next.Green, next.Blue);
    }

    private bool SetHudColor(string target, int red, int green, int blue)
    {
        red = ClampColor(red);
        green = ClampColor(green);
        blue = ClampColor(blue);

        switch (target.ToLowerInvariant())
        {
            case "time":
            case "timer":
                _config.TimerHudTimeRed = red;
                _config.TimerHudTimeGreen = green;
                _config.TimerHudTimeBlue = blue;
                return true;
            case "label":
            case "speedlabel":
                _config.TimerHudSpeedLabelRed = red;
                _config.TimerHudSpeedLabelGreen = green;
                _config.TimerHudSpeedLabelBlue = blue;
                return true;
            case "speed":
                _config.TimerHudSpeedRed = red;
                _config.TimerHudSpeedGreen = green;
                _config.TimerHudSpeedBlue = blue;
                return true;
            case "rank":
            case "detail":
                _config.TimerHudDetailRed = red;
                _config.TimerHudDetailGreen = green;
                _config.TimerHudDetailBlue = blue;
                return true;
            default:
                return false;
        }
    }

    private (int Red, int Green, int Blue) GetHudColor(string target)
    {
        return target.ToLowerInvariant() switch
        {
            "time" or "timer" => (_config.TimerHudTimeRed, _config.TimerHudTimeGreen, _config.TimerHudTimeBlue),
            "label" or "speedlabel" => (_config.TimerHudSpeedLabelRed, _config.TimerHudSpeedLabelGreen, _config.TimerHudSpeedLabelBlue),
            "speed" => (_config.TimerHudSpeedRed, _config.TimerHudSpeedGreen, _config.TimerHudSpeedBlue),
            "rank" or "detail" => (_config.TimerHudDetailRed, _config.TimerHudDetailGreen, _config.TimerHudDetailBlue),
            _ => (255, 255, 255),
        };
    }

    private static int ClampColor(int value) => Math.Clamp(value, 0, 255);

    private static readonly (int Red, int Green, int Blue)[] HudColorPalette =
    [
        (226, 67, 255),
        (80, 215, 255),
        (156, 255, 87),
        (255, 215, 64),
        (255, 92, 92),
        (175, 175, 185),
        (255, 255, 255),
    ];

    private RuntimeZone MatchZone(CBaseEntity entity, string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || targetName.StartsWith("surftimer_zone_", StringComparison.OrdinalIgnoreCase))
            return CreateZone(entity, targetName, ZoneType.Invalid);

        if (_config.StartZoneNames.Contains(targetName, StringComparer.OrdinalIgnoreCase))
            return CreateZone(entity, targetName, ZoneType.Start, track: 0);

        if (_config.EndZoneNames.Contains(targetName, StringComparer.OrdinalIgnoreCase))
            return CreateZone(entity, targetName, ZoneType.End, track: 0);

        if (TryMatch(StageZoneRegex, targetName, out var stage))
            return CreateZone(entity, targetName, ZoneType.Stage, track: 0, sequence: stage);

        if (TryMatch(CheckpointRegex, targetName, out var checkpoint))
            return CreateZone(entity, targetName, ZoneType.Checkpoint, track: 0, sequence: checkpoint);

        if (TryMatch(BonusStartRegex, targetName, out var bonusStart, defaultValue: 1))
            return CreateZone(entity, targetName, ZoneType.Start, track: bonusStart);

        if (TryMatch(BonusEndRegex, targetName, out var bonusEnd, defaultValue: 1))
            return CreateZone(entity, targetName, ZoneType.End, track: bonusEnd);

        var bonusCheckpoint = BonusCheckpointRegex.Match(targetName);
        if (bonusCheckpoint.Success &&
            int.TryParse(bonusCheckpoint.Groups["bonus"].Value, out var bonusTrack) &&
            int.TryParse(bonusCheckpoint.Groups["checkpoint"].Value, out var bonusCp))
            return CreateZone(entity, targetName, ZoneType.Checkpoint, track: bonusTrack, sequence: bonusCp);

        return CreateZone(entity, targetName, ZoneType.Invalid);
    }

    private static RuntimeZone CreateZone(CBaseEntity entity, string targetName, ZoneType type, int track = 0, int sequence = 0)
    {
        return new RuntimeZone
        {
            EntityIndex = entity.Index,
            TargetName = targetName,
            Type = type,
            Track = track,
            Sequence = sequence,
            Origin = entity.AbsOrigin,
            Angles = entity.AbsRotation,
            Mins = entity.Collision?.Mins,
            Maxs = entity.Collision?.Maxs,
        };
    }

    private static bool TryMatch(Regex regex, string targetName, out int value)
    {
        return TryMatch(regex, targetName, out value, defaultValue: -1);
    }

    private static bool TryMatch(Regex regex, string targetName, out int value, int defaultValue)
    {
        var match = regex.Match(targetName);
        if (match.Success && match.Groups["track"].Success && int.TryParse(match.Groups["track"].Value, out value))
            return true;

        if (match.Success)
        {
            value = defaultValue;
            return true;
        }

        value = defaultValue;
        return false;
    }

    private static readonly Regex StageZoneRegex = new(@"^(?:s|stage)(?<track>[1-9][0-9]?)_start$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CheckpointRegex = new(@"^map_(?:cp|checkpoint)(?<track>[1-9][0-9]?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BonusStartRegex = new(@"^(?:(?:b|bonus)(?<track>[1-9][0-9]?)?_start|timer_bonus(?<track>[1-9][0-9]?)_startzone)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BonusEndRegex = new(@"^(?:(?:b|bonus)(?<track>[1-9][0-9]?)?_end|timer_bonus(?<track>[1-9][0-9]?)_endzone)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BonusCheckpointRegex = new(@"^b(?:onus)?(?<bonus>[1-9]\d*)_c(?:heck)?p(?:oint)?(?<checkpoint>[1-9]\d*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
