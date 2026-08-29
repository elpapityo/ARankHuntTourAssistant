using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ARankHuntTourAssistant;

internal sealed class AutomationController
{
    private const uint IdyllshireTerritory = 478;
    private const uint HinterlandsTerritory = 399;
    private const uint YanxiaTerritory = 614;
    private readonly Configuration config;
    private readonly GameActions game;
    private readonly LanBridge lan;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ITargetManager targets;
    private readonly IChatGui chat;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly HuntRecordStore records;
    private readonly ConcurrentQueue<MobNotice> rx = new();
    private readonly Dictionary<uint, PassiveHuntTrack> passiveHunts = new();

    private sealed class PassiveHuntTrack
    {
        public required uint EntityId { get; init; }
        public required string Canonical { get; init; }
        public required uint TerritoryId { get; init; }
        public Vector3 LastPosition { get; set; }
        public DateTime LastSeen { get; set; }
        public DateTime LastEngaged { get; set; }
        public bool Engaged { get; set; }
    }

    private string? currentMob;
    private string? patrolMob;
    private int routeIndex;
    private Vector3? routeDestination;
    private DateTime routeStarted = DateTime.UtcNow;
    private DateTime stateSince = DateTime.UtcNow;
    private DateTime nextAction = DateTime.MinValue;
    private DateTime? targetMissing;
    private DateTime travelStarted = DateTime.MinValue;
    private bool travelCommandSent;
    private string travelStep = "";
    private bool ownsTarget;
    private bool chocoboSummonAttempted;
    private int chocoboSummonAttempts;
    private DateTime chocoboSummonRequestAt = DateTime.MinValue;
    private int landingRetryCount;
    private DateTime landingAttemptStarted = DateTime.MinValue;
    private Vector3? landingRecoveryDestination;
    private DateTime landingRecoveryStarted = DateTime.MinValue;
    private Vector3 childDestination;
    private Vector3? lastPatrolFlagDestination;
    private uint lastPatrolFlagTerritory;
    private readonly HashSet<(string Mob, int Index)> visitedPatrolPoints = new();
    private PatrolCandidate? currentPatrolCandidate;
    private int yanxiaPatrolGroup;
    private bool yanxiaNeedTeleport;
    private bool yanxiaSawBetweenAreas;
    private bool yanxiaStopRequested;
    private Vector3? yanxiaTeleportStartPosition;
    private Vector3? currentTargetPosition;
    private List<uint> sessionZones = new();
    private int sessionZoneIndex;
    private List<string> sessionWorlds = new();
    private int sessionWorldIndex;
    private Vector3? patrolStuckAnchor;
    private DateTime patrolStuckSince = DateTime.MinValue;
    private readonly Queue<(DateTime At, Vector3 Position)> patrolMotionSamples = new();
    private DateTime patrolLastMotionSampleAt = DateTime.MinValue;
    private DateTime lastFoundSoundAt = DateTime.MinValue;

    public bool Active { get; private set; }
    public HunterState State { get; private set; } = HunterState.Stopped;
    private string status = "停止";
    private readonly Queue<string> previousStatuses = new();
    public string Status
    {
        get => status;
        private set
        {
            if(value==status) return;
            var oldKey=StatusKey(status);
            var newKey=StatusKey(value);
            if(!string.IsNullOrWhiteSpace(status) && oldKey!=newKey)
            {
                previousStatuses.Enqueue(status);
                while(previousStatuses.Count>2) previousStatuses.Dequeue();
            }
            status=value;
        }
    }
    public IReadOnlyList<string> PreviousStatuses => previousStatuses.Reverse().ToArray();
    public HashSet<string> Completed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Found { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> NotFound { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,int> Progress { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<HuntRecord> SearchRecords => records.SearchRecords;
    public IReadOnlyList<HuntRecord> HuntRecords => records.HuntRecords;
    public IReadOnlyList<PatrolSkipRecord> SkipRecords => records.SkipRecords;
    public IReadOnlyList<HuntRecord> MobHuntRecords => records.MobHuntRecords;
    public string RecordDirectory => records.DirectoryPath;
    public void ExportSearchRecords()=>records.ExportSearch();
    public void ExportHuntRecords()=>records.ExportHunt();
    public void ExportSkipRecords()=>records.ExportSkips();
    public void ExportMobHuntRecords()=>records.ExportMobHunt();
    public void DeleteSearchRecord(HuntRecord record)=>records.DeleteSearch(record);
    public void DeleteHuntRecord(HuntRecord record)=>records.DeleteHunt(record);
    public void DeleteSkipRecord(PatrolSkipRecord record)=>records.DeleteSkip(record);
    public void ClearSearchRecords()=>records.ClearSearch();
    public void ClearHuntRecords()=>records.ClearHunt();
    public void ClearSkipRecords()=>records.ClearSkips();
    public void AddMobHuntRecord(HuntRecord record)=>records.AddMobHunt(record);
    public void DeleteMobHuntRecord(HuntRecord record)=>records.DeleteMobHunt(record);
    public void ClearMobHuntRecords()=>records.ClearMobHunt();
    public void MoveMobHuntRecord(int index,int delta)=>records.MoveMobHunt(index,delta);
    public string SetRecordDirectory(string? path)=>records.SetDirectory(path);
    public string ResetRecordDirectory()=>records.ResetDirectory();
    public unsafe bool PutRecordInChat(HuntRecord record)
    {
        try
        {
            var territory=data.GetExcelSheet<TerritoryType>().GetRowOrDefault(record.TerritoryId);
            var mapId=territory?.Map.RowId ?? 0u;
            if(mapId==0) return false;

            // 記録クリック時はチャットリンクだけでなく、ゲーム側の実MAPフラッグも同じ座標へ立てる。
            // 現在地とは別MAPの記録でも、記録が持つTerritory/MapIdをそのまま使用する。
            try
            {
                var payload=new MapLinkPayload(record.TerritoryId,mapId,record.MapX,record.MapY);
                var map=AgentMap.Instance();
                if(map!=null)
                {
                    var flagPosition=new Vector3(payload.RawX/1000f,0f,payload.RawY/1000f);
                    map->SetFlagMapMarker(record.TerritoryId,mapId,flagPosition);
                    log.Information("[AMH] 記録クリックでMAPフラッグ設定 / Territory={Territory} MapId={MapId} Mob={Mob} MAP={X:0.0},{Y:0.0}",record.TerritoryId,mapId,record.Mob,record.MapX,record.MapY);
                }
            }
            catch(Exception flagEx) { log.Debug(flagEx,"[AMH] 記録クリックのMAPフラッグ設定に失敗"); }

            // ChatLog の入力欄 SetText は SeString のバイナリPayloadを保持できず、
            // CreateMapLink を直接入れても手動送信時にクリックMAPリンクにならない。
            // 先にゲーム側へ実MAPフラッグを立て、入力欄にはFFXIV標準の <flag> 代名詞を入れる。
            // ユーザーが手動送信した時に <flag> が現在の旗座標のクリック可能MAPリンクへ展開される。
            var chatText=$"{record.Mob} ({record.MapX:0.0}, {record.MapY:0.0}) <flag>";
            var ok=game.SetChatInput(System.Text.Encoding.UTF8.GetBytes(chatText));
            if(ok) log.Information("[AMH] 記録をチャット入力欄へ設定 / {Mob} MAP={X:0.0},{Y:0.0}",record.Mob,record.MapX,record.MapY);
            else Print("チャット入力欄を取得できませんでした。チャット欄を一度アクティブにして再度クリックしてください。");
            return ok;
        }
        catch(Exception ex) { log.Debug(ex,"[AMH] record chat input failed"); return false; }
    }

    public AutomationController(Configuration config, GameActions game, LanBridge lan, IObjectTable objects,
        IClientState clientState, IPlayerState playerState, ITargetManager targets, IChatGui chat, IDataManager data, IPluginLog log, HuntRecordStore records)
    {
        this.config=config; this.game=game; this.lan=lan; this.objects=objects; this.clientState=clientState;
        this.playerState=playerState; this.targets=targets; this.chat=chat; this.data=data; this.log=log; this.records=records;
        lan.PacketReceived += p => rx.Enqueue(p);
    }

    public HuntZoneInfo? Zone => HuntData.GetZone(config.SelectedTerritoryId);

    // 実機テスト用：現在向かっている最寄り巡回候補地点をUIから確認できるようにする。
    public string? CurrentPatrolMobName => currentPatrolCandidate is { } c ? Display(c.Mob) : null;
    public int CurrentPatrolPointNumber => currentPatrolCandidate is { } c ? c.Index + 1 : 0;
    public int CurrentPatrolPointCount => currentPatrolCandidate is { } c
        ? Zone?.Mobs.FirstOrDefault(m=>m.CanonicalName.Equals(c.Mob,StringComparison.OrdinalIgnoreCase))?.RoutePoints.Length ?? 0
        : 0;
    public (float X, float Z)? CurrentPatrolMapPoint
        => currentPatrolCandidate is { } c ? (c.Point.X, c.Point.Y) : null;
    public Vector3? CurrentPatrolInternalPoint => routeDestination;

    public void Start()
    {
        config.Role="Solo"; config.HuntMode="Search"; config.AlwaysRecordHunts=true;
        StopOwned(false);
        Completed.Clear(); Found.Clear(); NotFound.Clear(); Progress.Clear();
        currentMob=null; patrolMob=null; routeIndex=0; routeDestination=null; chocoboSummonAttempted=false; chocoboSummonAttempts=0; chocoboSummonRequestAt=DateTime.MinValue;
        ResetLandingRecovery();
        visitedPatrolPoints.Clear(); currentPatrolCandidate=null; currentTargetPosition=null;
        ResetPatrolStuckWatch();
        ResetTravel(); ClearFlag();
        InitializePatrolSession();
        if(config.Role!="Child" && sessionZones.Count==0){ Set(HunterState.Stopped,"巡回対象MAPがありません"); Print("選択した追加ディスクの巡回可能MAPがすべて除外されています。"); return; }
        var zone=Zone;
        if(zone==null){ Set(HunterState.Stopped,"探索MAPが選択されていません"); return; }
        if(config.Role!="Child" && IsMapExcluded(zone.TerritoryId)){ Set(HunterState.Stopped,$"{zone.JapaneseName}は除外MAPです"); Print($"{zone.JapaneseName}はMAPブラックリストに登録されています。"); return; }
        yanxiaPatrolGroup=0;
        yanxiaNeedTeleport=config.Role!="Child" && zone.TerritoryId==YanxiaTerritory;
        yanxiaSawBetweenAreas=false;
        yanxiaStopRequested=false;
        yanxiaTeleportStartPosition=null;
        if(config.Role!="Child" && !zone.HasPatrolData){ Set(HunterState.Stopped,"このMAPは実巡回座標が未登録です"); Print("実座標が未登録のMAPでは開始しません。"); return; }
        Active=true;
        if(config.Role=="Child") Set(HunterState.ChildWaiting,"親機からAモブ情報を待っています");
        else Set(HunterState.PreparingZone,"探索先を確認中");
    }

    public void Stop()
    {
        game.ForceStopVnav();
        StopOwned(true);
        Print("停止しました。");
    }

    // 実機テスト用：現在向かっている巡回候補だけを確認済みにして次へ進む。
    public void SkipCurrentPatrolPoint() => SkipCurrentPatrolPoint("手動スキップ",false);

    private void SkipCurrentPatrolPoint(string reason,bool automatic)
    {
        if(!Active || State!=HunterState.Patrol || currentPatrolCandidate is not { } candidate) return;

        RecordSkippedPatrolPoint(candidate,reason,automatic);
        visitedPatrolPoints.Add(candidate.Key);
        UpdateProgressForVisitedPoint(candidate.Mob);
        log.Information("[AMH] 巡回地点をスキップ / {Mob} index={Index} MAP={X:0.0},{Y:0.0} reason={Reason} auto={Auto}",
            Display(candidate.Mob), candidate.Index+1, candidate.Point.X, candidate.Point.Y, reason, automatic);

        game.StopOwnedVnav();
        ClearFlag();
        routeDestination=null;
        currentPatrolCandidate=null;
        routeStarted=DateTime.UtcNow;
        ResetPatrolStuckWatch();
        Status="現在の巡回地点をスキップしました。次の候補を選択します";
    }

    private void RecordSkippedPatrolPoint(PatrolCandidate candidate,string reason,bool automatic)
    {
        var zone=Zone; if(zone==null) return;
        var pos=objects.LocalPlayer?.Position ?? candidate.World;
        records.AddSkip(new PatrolSkipRecord(DateTime.UtcNow,CurrentWorldName(),zone.Expansion,zone.TerritoryId,zone.JapaneseName,Display(candidate.Mob),candidate.Index+1,candidate.Point.X,candidate.Point.Y,pos.X,pos.Y,pos.Z,reason,Math.Clamp(config.AutoSkipStuckSeconds,3,120),automatic));
    }

    private void StopOwned(bool stopped)
    {
        Active=false;
        game.StopOwnedVnav(); game.SetCombat(false);
        if(ownsTarget){ try{ targets.Target=null; }catch{} ownsTarget=false; }
        currentMob=null; patrolMob=null; routeDestination=null; targetMissing=null; currentTargetPosition=null;
        currentPatrolCandidate=null;
        yanxiaPatrolGroup=0; yanxiaNeedTeleport=false; yanxiaSawBetweenAreas=false; yanxiaStopRequested=false; yanxiaTeleportStartPosition=null;
        ResetTravel(); ClearFlag();
        while(rx.TryDequeue(out _)){}
        if(stopped) Set(HunterState.Stopped,"停止");
    }

    public void Tick()
    {
        try
        {
            TickPassiveHuntRecorder();
            if(!Active) return;
            if(config.Role=="Child") TickChild(); else TickMain();
        }
        catch(Exception ex){ log.Error(ex,"[AMH] automation"); Set(HunterState.Error,"エラー：/xllog を確認してください"); Active=false; }
    }

    private void TickPassiveHuntRecorder()
    {
        if(!config.AlwaysRecordHunts)
        {
            passiveHunts.Clear();
            return;
        }

        var zone=HuntData.GetZone(clientState.TerritoryType);
        if(zone==null)
        {
            passiveHunts.Clear();
            return;
        }

        var now=DateTime.UtcNow;
        var hate=game.HateEntityIds().ToHashSet();
        var seen=new HashSet<uint>();
        foreach(var o in objects)
        {
            var canonical=CanonicalInZone(zone,o.Name.TextValue);
            if(canonical==null) continue;
            seen.Add(o.EntityId);

            if(!passiveHunts.TryGetValue(o.EntityId,out var track) || track.TerritoryId!=zone.TerritoryId)
            {
                track=new PassiveHuntTrack
                {
                    EntityId=o.EntityId, Canonical=canonical, TerritoryId=zone.TerritoryId,
                    LastPosition=o.Position, LastSeen=now, LastEngaged=DateTime.MinValue, Engaged=false
                };
                passiveHunts[o.EntityId]=track;
            }

            track.LastPosition=o.Position;
            track.LastSeen=now;
            var isCurrentTarget = targets.Target?.EntityId == o.EntityId;
            if(hate.Contains(o.EntityId) || (isCurrentTarget && game.IsInCombat) ||
               (currentMob!=null && currentMob.Equals(canonical,StringComparison.OrdinalIgnoreCase) && game.IsInCombat))
            {
                track.Engaged=true;
                track.LastEngaged=now;
            }

            if(o.IsDead && track.Engaged)
            {
                RecordPassiveHunt(zone,track);
                passiveHunts.Remove(o.EntityId);
            }
        }

        // Aモブは討伐直後にObjectTableから消え、IsDead=trueを観測できない場合がある。
        // 一度交戦を確認した個体が消え、戦闘も終了した場合は討伐として記録する。
        foreach(var kv in passiveHunts.ToArray())
        {
            var track=kv.Value;
            if(track.TerritoryId!=zone.TerritoryId)
            {
                passiveHunts.Remove(kv.Key);
                continue;
            }

            if(seen.Contains(kv.Key)) continue;
            var missingSeconds=(now-track.LastSeen).TotalSeconds;
            var engagedAgo=(now-track.LastEngaged).TotalSeconds;
            if(track.Engaged && missingSeconds>=0.75 && engagedAgo<=20 && !game.IsInCombat)
            {
                log.Information("[AMH][HUNT-RECORD] 交戦済みAモブの消失＋戦闘終了を討伐として判定 / Mob={Mob} Entity={Entity:X8} Missing={Missing:0.00}s",track.Canonical,track.EntityId,missingSeconds);
                RecordPassiveHunt(zone,track);
                passiveHunts.Remove(kv.Key);
                continue;
            }

            // 長時間見えない個体だけ破棄。短時間の消失は討伐判定のため保持する。
            if(missingSeconds>30) passiveHunts.Remove(kv.Key);
        }
    }

    private void RecordPassiveHunt(HuntZoneInfo zone, PassiveHuntTrack track)
    {
        var map=WorldToMapPoint(zone,track.LastPosition);
        var display=zone.Mobs.FirstOrDefault(m=>m.CanonicalName.Equals(track.Canonical,StringComparison.OrdinalIgnoreCase))?.DisplayName ?? track.Canonical;
        records.AddHunt(new HuntRecord(DateTime.UtcNow,CurrentWorldName(),zone.Expansion,zone.TerritoryId,zone.JapaneseName,display,map.X,map.Y,track.LastPosition.X,track.LastPosition.Y,track.LastPosition.Z,"討伐"));
        log.Information("[AMH][HUNT-RECORD] 常時監視で討伐記録 / World={World} Territory={Territory} Mob={Mob} Entity={Entity:X8}",CurrentWorldName(),zone.TerritoryId,display,track.EntityId);
    }

    private static string? CanonicalInZone(HuntZoneInfo zone,string name)
    {
        foreach(var m in zone.Mobs)
            if(name.Equals(m.CanonicalName,StringComparison.OrdinalIgnoreCase)||name.Equals(m.DisplayName,StringComparison.OrdinalIgnoreCase))
                return m.CanonicalName;
        return null;
    }

    private void TickMain()
    {
        var zone=Zone; if(zone==null){ StopOwned(true); return; }
        if(!EnsureWorldAndZone(zone)) return;
        if(State is HunterState.PreparingZone or HunterState.Travelling) Set(HunterState.Patrol,"巡回開始");

        if(State==HunterState.Patrol)
        {
            var found=FindAnyTarget();
            if(found.Object!=null && found.Canonical!=null){ _=lan.SendMobAsync(found.Canonical,zone.TerritoryId,found.Object.Position); if(config.HuntMode=="Search") RecordSearchAndContinue(zone,found.Canonical,found.Object); else BeginTarget(found.Canonical,found.Object,false); return; }
            if(!EnsureMountedAndFlying()) return;
            TickPatrol(zone); return;
        }
        if(State==HunterState.ApproachingTarget){ TickTargetApproach(false); return; }
        if(State==HunterState.Landing){ TickLanding(false); return; }
        if(State==HunterState.PreparingCompanion){ TickCompanion(false); return; }
        if(State==HunterState.Combat){ TickCombat(false); return; }
        if(State==HunterState.Remounting){ TickRemount(false); return; }
    }

    private void TickChild()
    {
        if(State==HunterState.ChildWaiting)
        {
            while(rx.TryDequeue(out var p))
            {
                if(p.Type!="MOB") continue;
                var packetZone=HuntData.GetZone(p.Territory);
                if(packetZone==null || !packetZone.Mobs.Any(m=>m.CanonicalName.Equals(p.Mob,StringComparison.OrdinalIgnoreCase)))
                {
                    log.Warning("[AMH][CHILD] 未登録通知を無視 / Territory={Territory} Mob={Mob}",p.Territory,p.Mob);
                    continue;
                }

                // 親機の通知を正として、子機側の選択MAPを自動で合わせる。
                // これにより子機で同じMAPを手動選択していなくても、そのまま移動・追従できる。
                config.SelectedTerritoryId=p.Territory;
                config.SelectedExpansion=packetZone.Expansion;
                currentMob=p.Mob;
                childDestination=p.Position;
                ResetTravel();
                log.Information("[AMH][CHILD] 親機通知受信 / Territory={Territory} Mob={Mob} Position={X:0.0},{Y:0.0},{Z:0.0}",
                    p.Territory,p.Mob,p.X,p.Y,p.Z);
                Set(HunterState.ChildApproach,$"親機から{packetZone.Mobs.First(m=>m.CanonicalName.Equals(p.Mob,StringComparison.OrdinalIgnoreCase)).DisplayName}を受信");
                break;
            }
            return;
        }
        var zone=Zone; if(zone==null) return;
        if(!EnsureWorldAndZone(zone)) return;
        if(State==HunterState.ChildApproach)
        {
            if(currentMob==null){ Set(HunterState.ChildWaiting,"親機待機"); return; }
            var visible=FindByCanonical(currentMob); if(visible!=null){ BeginTarget(currentMob,visible,true); return; }
            if((DateTime.UtcNow-stateSince).TotalSeconds>60){ game.StopOwnedVnav(); currentMob=null; Set(HunterState.ChildWaiting,"対象確認失敗・親機待機"); return; }
            if(!EnsureMountedAndFlying()) return;
            if(!game.IsBusy()) game.Move(childDestination,true);
            Status=$"{Display(currentMob)}の発見位置へ移動中"; return;
        }
        if(State==HunterState.ApproachingTarget){ TickTargetApproach(true); return; }
        if(State==HunterState.Landing){ TickLanding(true); return; }
        if(State==HunterState.PreparingCompanion){ TickCompanion(true); return; }
        if(State==HunterState.Combat){ TickCombat(true); return; }
        if(State==HunterState.Remounting){ TickRemount(true); return; }
    }

    private void InitializePatrolSession()
    {
        sessionZones = (config.PatrolExpansionMaps || config.PatrolWorlds
            ? HuntData.GetZones(config.SelectedExpansion).Where(z=>z.HasPatrolData && !IsMapExcluded(z.TerritoryId)).Select(z=>z.TerritoryId)
            : new[]{config.SelectedTerritoryId}.Where(id=>!IsMapExcluded(id))).Distinct().ToList();
        if(sessionZones.Count==0 && !IsMapExcluded(config.SelectedTerritoryId)) sessionZones.Add(config.SelectedTerritoryId);
        if(config.PatrolExpansionMaps || config.PatrolWorlds)
        {
            var start=sessionZones.IndexOf(config.SelectedTerritoryId);
            if(start<0) start=0;
            sessionZones=sessionZones.Skip(start).Concat(sessionZones.Take(start)).ToList();
        }
        sessionZoneIndex=0;
        config.SelectedTerritoryId=sessionZones[0];

        var startWorld=config.SelectedWorld=="現在のワールド" ? CurrentWorldName() : config.SelectedWorld;
        sessionWorlds.Clear(); sessionWorldIndex=0;
        if(config.PatrolWorlds)
        {
            var worlds=HuntData.GetWorlds(config.SelectedDataCenter);
            var start=Array.FindIndex(worlds,w=>w.Equals(startWorld,StringComparison.OrdinalIgnoreCase));
            if(start<0) start=0;
            for(int i=0;i<worlds.Length;i++) sessionWorlds.Add(worlds[(start+i)%worlds.Length]);
            config.SelectedWorld=sessionWorlds[0];
        }
        else if(config.SelectedWorld!="現在のワールド") sessionWorlds.Add(config.SelectedWorld);
        else sessionWorlds.Add(startWorld);
    }

    private bool AdvancePatrolScope(HuntZoneInfo finishedZone)
    {
        var multiZone=config.PatrolExpansionMaps || config.PatrolWorlds;
        var multiWorld=config.PatrolWorlds;
        if(!multiZone && !multiWorld) return false;

        if(multiZone && sessionZoneIndex+1<sessionZones.Count)
        {
            sessionZoneIndex++;
            SwitchPatrolZone(sessionZones[sessionZoneIndex],$"次のMAPへ：{HuntData.GetZone(sessionZones[sessionZoneIndex])?.JapaneseName}");
            return true;
        }

        if(multiWorld && sessionWorldIndex+1<sessionWorlds.Count)
        {
            sessionWorldIndex++;
            sessionZoneIndex=0;
            config.SelectedWorld=sessionWorlds[sessionWorldIndex];
            config.SelectedTerritoryId=sessionZones[0];
            ResetPatrolForNextScope();
            ResetTravel();
            Set(HunterState.PreparingZone,$"次のワールドへ：{config.SelectedWorld}");
            log.Information("[AMH] ワールド巡回切替 / {World} / 最初のMAP={Territory}",config.SelectedWorld,config.SelectedTerritoryId);
            return true;
        }
        return false;
    }

    private void SwitchPatrolZone(uint territory,string status)
    {
        config.SelectedTerritoryId=territory;
        ResetPatrolForNextScope();
        ResetTravel();
        Set(HunterState.PreparingZone,status);
        log.Information("[AMH] 追加ディスク内MAP巡回切替 / Territory={Territory}",territory);
    }

    private void ResetPatrolForNextScope()
    {
        game.StopOwnedVnav(); ClearFlag();
        Found.Clear(); NotFound.Clear(); Progress.Clear(); Completed.Clear();
        visitedPatrolPoints.Clear(); currentPatrolCandidate=null; routeDestination=null; currentMob=null; currentTargetPosition=null;
        yanxiaPatrolGroup=0; yanxiaNeedTeleport=config.SelectedTerritoryId==YanxiaTerritory; yanxiaSawBetweenAreas=false; yanxiaStopRequested=false; yanxiaTeleportStartPosition=null;
    }


    [DllImport("user32.dll", SetLastError=false)]
    private static extern bool MessageBeep(uint uType);

    private void PlayFoundSound()
    {
        if(!config.PlaySoundOnFound) return;
        var now=DateTime.UtcNow;
        if((now-lastFoundSoundAt).TotalMilliseconds<1500) return;
        lastFoundSoundAt=now;
        try { MessageBeep(0x00000040); } catch { } // MB_ICONASTERISK
    }

    private void RecordSearchAndContinue(HuntZoneInfo zone,string canonical,IGameObject obj)
    {
        PlayFoundSound();
        var map=WorldToMapPoint(zone,obj.Position);
        var world=CurrentWorldName();
        var record=new HuntRecord(DateTime.UtcNow,world,zone.Expansion,zone.TerritoryId,zone.JapaneseName,Display(canonical),map.X,map.Y,obj.Position.X,obj.Position.Y,obj.Position.Z,"探索");
        records.AddSearch(record);
        Found.Add(canonical);
        foreach(var c in BuildActivePatrolCandidates(zone).Where(c=>c.Mob.Equals(canonical,StringComparison.OrdinalIgnoreCase))) visitedPatrolPoints.Add(c.Key);
        Progress[canonical]=GetMob(canonical)?.RoutePoints.Length??0;
        game.StopOwnedVnav(); ClearFlag(); routeDestination=null; currentPatrolCandidate=null;
        log.Information("[AMH][SEARCH] 発見記録 / World={World} Territory={Territory} Mob={Mob} MAP={MapX:0.0},{MapY:0.0} XYZ={X:0.00},{Y:0.00},{Z:0.00}",world,zone.TerritoryId,Display(canonical),map.X,map.Y,obj.Position.X,obj.Position.Y,obj.Position.Z);
        Print($"{Display(canonical)}を発見：MAP {map.X:0.0}, {map.Y:0.0} を記録して巡回を続行します。");
        Status=$"{Display(canonical)}を記録しました。次の巡回地点へ";
    }

    private void RecordHuntResult(string canonical,Vector3? position)
    {
        var zone=Zone; if(zone==null || !position.HasValue) return;
        var map=WorldToMapPoint(zone,position.Value);
        records.AddHunt(new HuntRecord(DateTime.UtcNow,CurrentWorldName(),zone.Expansion,zone.TerritoryId,zone.JapaneseName,Display(canonical),map.X,map.Y,position.Value.X,position.Value.Y,position.Value.Z,"討伐"));
    }

    private (float X,float Y) WorldToMapPoint(HuntZoneInfo zone,Vector3 world)
    {
        // MapLinkPayloadによる既存のMAP→内部座標変換を2点取り、同じ線形変換を逆算する。
        var a=MapPointToFlagWorld(zone,(1f,1f));
        var b=MapPointToFlagWorld(zone,(2f,2f));
        if(!a.HasValue || !b.HasValue) return (0,0);
        var dx=b.Value.X-a.Value.X; var dz=b.Value.Z-a.Value.Z;
        var mx=MathF.Abs(dx)<0.0001f?0f:1f+(world.X-a.Value.X)/dx;
        var my=MathF.Abs(dz)<0.0001f?0f:1f+(world.Z-a.Value.Z)/dz;
        return (mx,my);
    }

    private bool EnsureWorldAndZone(HuntZoneInfo zone)
    {
        var selectedWorld=config.SelectedWorld;
        if(selectedWorld!="現在のワールド")
        {
            var current=CurrentWorldName();
            if(!string.Equals(current,selectedWorld,StringComparison.OrdinalIgnoreCase))
            {
                if(game.IsBetweenAreas){ Status=$"{selectedWorld}へワールド移動中"; return false; }
                if(travelStep!="WORLD") ResetTravel("WORLD");
                if(!travelCommandSent){ game.StopOwnedVnav(); game.SetCombat(false); travelCommandSent=true; travelStarted=DateTime.UtcNow; game.Aethernet(selectedWorld); Status=$"{selectedWorld}へワールド移動要求中"; return false; }
                if((DateTime.UtcNow-travelStarted).TotalSeconds>120){ travelCommandSent=false; travelStarted=DateTime.UtcNow; Status="ワールド移動を再試行します"; }
                return false;
            }
        }

        if(zone.TerritoryId==YanxiaTerritory && config.Role!="Child" && yanxiaNeedTeleport)
        {
            Set(HunterState.Travelling,$"ヤンサ {YanxiaGroupName()}へ移動中");
            return TickYanxiaGroupTeleport();
        }

        if(clientState.TerritoryType==zone.TerritoryId){ if(travelStep!="") ResetTravel(); return true; }
        Set(HunterState.Travelling,$"{zone.JapaneseName}へ移動中");
        return TickZoneTravel(zone);
    }

    private string YanxiaGroupName() => yanxiaPatrolGroup==0 ? "烈士庵側" : "ナマイ村側";
    private string YanxiaAetheryte() => yanxiaPatrolGroup==0 ? "烈士庵" : "ナマイ村";
    private static int YanxiaGroupFor((float X,float Y) point) => point.Y < 24f ? 0 : 1;

    private bool TickYanxiaGroupTeleport()
    {
        var step=$"YANXIA_{yanxiaPatrolGroup}";
        if(travelStep!=step)
        {
            ResetTravel(step);
            yanxiaSawBetweenAreas=false;
            yanxiaStopRequested=false;
            yanxiaTeleportStartPosition=null;
        }

        if(game.IsBetweenAreas)
        {
            yanxiaSawBetweenAreas=true;
            Status=$"ヤンサ {YanxiaGroupName()}へテレポ中";
            return false;
        }

        // FATEの同一MAPテレポと同じ考え方で、コマンド送信だけでは成功扱いにしない。
        // BetweenAreasを通過したか、送信前位置から十分離れたことを実際に確認してから巡回へ戻す。
        if(travelCommandSent)
        {
            var player=objects.LocalPlayer;
            var moved=player!=null && yanxiaTeleportStartPosition.HasValue &&
                      Flat(player.Position,yanxiaTeleportStartPosition.Value)>=30f;
            if(yanxiaSawBetweenAreas || moved)
            {
                log.Information("[AMH] ヤンサ同一MAPテレポ完了確認 / 起点={Aetheryte} / BetweenAreas={BetweenAreas} / Moved={Moved}",
                    YanxiaAetheryte(), yanxiaSawBetweenAreas, moved);
                yanxiaNeedTeleport=false;
                yanxiaSawBetweenAreas=false;
                yanxiaStopRequested=false;
                yanxiaTeleportStartPosition=null;
                ResetTravel();
                Set(HunterState.Patrol,$"ヤンサ {YanxiaGroupName()}の巡回開始");
                return true;
            }

            if((DateTime.UtcNow-travelStarted).TotalSeconds>30)
            {
                log.Warning("[AMH] ヤンサ同一MAPテレポ確認失敗 / 起点={Aetheryte} / 30秒経過のため再試行", YanxiaAetheryte());
                travelCommandSent=false;
                yanxiaSawBetweenAreas=false;
                yanxiaStopRequested=false;
                yanxiaTeleportStartPosition=null;
                Status=$"{YanxiaAetheryte()}へのテレポを再試行します";
            }
            return false;
        }

        // FATEのStopVnavForSameMapTeleportを参考に、先にvnavmeshを確実に止めてから
        // Lifestreamへ同一MAPテレポを要求する。停止要求直後にはテレポを重ねない。
        if(!yanxiaStopRequested)
        {
            game.ForceStopVnav();
            yanxiaStopRequested=true;
            travelStarted=DateTime.UtcNow;
            Status=$"{YanxiaAetheryte()}へテレポ前：vnavmesh停止待ち";
            return false;
        }

        if(game.IsBusy())
        {
            if((DateTime.UtcNow-travelStarted).TotalSeconds>2)
            {
                game.ForceStopVnav();
                travelStarted=DateTime.UtcNow;
            }
            Status=$"{YanxiaAetheryte()}へテレポ前：vnavmesh停止待ち";
            return false;
        }

        if(game.IsInCombat)
        {
            Status=$"{YanxiaAetheryte()}へテレポ前：戦闘終了待ち";
            return false;
        }

        var local=objects.LocalPlayer;
        yanxiaTeleportStartPosition=local?.Position;
        travelCommandSent=true;
        travelStarted=DateTime.UtcNow;
        game.Teleport(YanxiaAetheryte());
        log.Information("[AMH] ヤンサ同一MAPテレポ要求 / 起点={Aetheryte} / Start={Position}",
            YanxiaAetheryte(), yanxiaTeleportStartPosition);
        Status=$"{YanxiaAetheryte()}へテレポ要求中";
        return false;
    }

    private bool TickZoneTravel(HuntZoneInfo zone)
    {
        if(game.IsBetweenAreas){ Status=$"{zone.JapaneseName}へエリア移動中"; return false; }
        if(zone.TerritoryId==HinterlandsTerritory)
        {
            if(clientState.TerritoryType!=IdyllshireTerritory)
            {
                if(travelStep!="IDYLL") ResetTravel("IDYLL");
                if(!travelCommandSent){ var a="イディルシャイア"; travelCommandSent=true; travelStarted=DateTime.UtcNow; game.Teleport(a); Status="イディルシャイアへテレポ要求中"; }
                else if((DateTime.UtcNow-travelStarted).TotalSeconds>90){ travelCommandSent=false; }
                return false;
            }
            if(travelStep!="GATE") ResetTravel("GATE");
            if(!travelCommandSent){ game.StopOwnedVnav(); travelCommandSent=true; travelStarted=DateTime.UtcNow; game.Aethernet("表紙門"); Status="表紙門へ都市内直接転送中"; return false; }
            if((DateTime.UtcNow-travelStarted).TotalSeconds>30){ travelCommandSent=false; Status="表紙門への転送を再試行します"; }
            return false;
        }

        var name=DirectAetheryte(zone.TerritoryId);
        if(string.IsNullOrWhiteSpace(name)){ Status=$"{zone.JapaneseName}の直接テレポ先を取得できません"; return false; }
        if(travelStep!="TP") ResetTravel("TP");
        if(!travelCommandSent){ game.StopOwnedVnav(); travelCommandSent=true; travelStarted=DateTime.UtcNow; game.Teleport(name); Status=$"{name}へテレポ要求中"; return false; }
        if((DateTime.UtcNow-travelStarted).TotalSeconds>90){ travelCommandSent=false; Status=$"{name}へのテレポを再試行します"; }
        return false;
    }

    private void TickPatrol(HuntZoneInfo zone)
    {
        var player=objects.LocalPlayer; if(player==null) return;

        // 候補は「モブ名 + そのモブ内の地点番号」で別管理する。
        // 同じMAP座標でも別モブの候補は統合しない。
        var allCandidates=BuildActivePatrolCandidates(zone);
        if(allCandidates.Count==0)
        {
            if(AdvancePatrolScope(zone)) return;
            Active=false; currentPatrolCandidate=null; routeDestination=null; ClearFlag();
            Set(HunterState.Complete,"対象Aモブの巡回完了");
            return;
        }

        var candidates=FilterCurrentPatrolArea(zone,allCandidates);
        var remaining=candidates.Where(c=>!visitedPatrolPoints.Contains(c.Key)).ToList();
        if(remaining.Count==0)
        {
            if(TryAdvanceYanxiaArea(zone,allCandidates)) return;
            foreach(var mob in zone.Mobs.Where(m=>Searchable(m.CanonicalName) && m.RoutePoints.Length>0))
                NotFound.Add(mob.CanonicalName);
            if(AdvancePatrolScope(zone)) return;
            Active=false; currentPatrolCandidate=null; routeDestination=null; ClearFlag();
            Set(HunterState.Complete,"対象Aモブの全候補地点を確認しました");
            return;
        }

        if(currentPatrolCandidate==null || routeDestination==null ||
           !remaining.Any(c=>CurrentCandidateMatches(c)))
        {
            SelectNextPatrolPoint(zone, player.Position, remaining);
            if(routeDestination==null) return;
        }

        var point=currentPatrolCandidate!.Point;

        // 巡回中だけスタック監視。約3m以内から10秒以上ほぼ動かなければ、
        // ユーザー設定がONのとき現在候補をスキップする。戦闘・着地・テレポ処理では呼ばれない。
        if(CheckAndSkipStuckPatrol(player.Position)) return;

        bool reached=Flat(player.Position,routeDestination!.Value)<=18f;
        bool timeout=(DateTime.UtcNow-routeStarted).TotalSeconds>=20 && !game.IsBusy();
        if(reached||timeout)
        {
            var current=remaining.FirstOrDefault(c=>CurrentCandidateMatches(c));
            if(current!=null)
            {
                visitedPatrolPoints.Add(current.Key);
                UpdateProgressForVisitedPoint(current.Mob);
            }

            game.StopOwnedVnav(); routeDestination=null; currentPatrolCandidate=null; routeStarted=DateTime.UtcNow; ResetPatrolStuckWatch();

            allCandidates=BuildActivePatrolCandidates(zone);
            candidates=FilterCurrentPatrolArea(zone,allCandidates);
            remaining=candidates.Where(c=>!visitedPatrolPoints.Contains(c.Key)).ToList();
            if(remaining.Count==0)
            {
                if(TryAdvanceYanxiaArea(zone,allCandidates)) return;
                foreach(var mob in zone.Mobs.Where(m=>Searchable(m.CanonicalName) && m.RoutePoints.Length>0))
                    NotFound.Add(mob.CanonicalName);
                if(AdvancePatrolScope(zone)) return;
                Active=false; ClearFlag(); Set(HunterState.Complete,"対象Aモブの全候補地点を確認しました"); return;
            }
            SelectNextPatrolPoint(zone, player.Position, remaining);
            if(routeDestination==null) return;
            point=currentPatrolCandidate!.Point;
        }

        // 旗がゲーム側で消えていたら、/vnav flyflag を出す前に必ず立て直す。
        if(!PatrolFlagPresent())
        {
            routeDestination=UpdateFlag(zone,point);
            if(routeDestination==null)
            {
                Status=$"巡回地点 {point.X:0.0}, {point.Y:0.0} の旗を確認できません。再設定待ち";
                return;
            }
            routeStarted=DateTime.UtcNow;
        }

        if(!game.IsBusy()) game.FlyToFlag();
        var ownerText=currentPatrolCandidate is { } activeCandidate ? Display(activeCandidate.Mob) : "Aモブ";
        var modeText=config.PatrolMode=="Mob" ? "モブ単位巡回中" : "最寄り巡回中";
        var activeIndex=currentPatrolCandidate?.Index+1 ?? 0;
        var activeTotal=currentPatrolCandidate is { } ac ? zone.Mobs.FirstOrDefault(m=>m.CanonicalName.Equals(ac.Mob,StringComparison.OrdinalIgnoreCase))?.RoutePoints.Length ?? 0 : 0;
        Status=$"{modeText} {ownerText} #{activeIndex}/{activeTotal} MAP {point.X:0.0}, {point.Y:0.0}";
    }

    private bool CheckAndSkipStuckPatrol(Vector3 playerPosition)
    {
        if(!config.AutoSkipStuckPatrolPoint)
        {
            ResetPatrolStuckWatch();
            return false;
        }

        // 巡回対象が無い時、vnavmeshのメッシュがまだ利用可能でない時、
        // または経路作成(Pathfind)中はスタック時間に含めない。
        // Path.IsRunning は実際の巡回移動なので監視対象にする。
        if(currentPatrolCandidate==null || routeDestination==null || !game.IsReady() || game.IsPathfinding())
        {
            ResetPatrolStuckWatch();
            return false;
        }

        var now=DateTime.UtcNow;

        // 完全停止～小さなふらつき：3m以内から設定秒数出られなければ停滞。
        var stuckSeconds=Math.Clamp(config.AutoSkipStuckSeconds,3,120);
        if(patrolStuckAnchor==null || Flat(playerPosition,patrolStuckAnchor.Value)>3f)
        {
            patrolStuckAnchor=playerPosition;
            patrolStuckSince=now;
        }
        else
        {
            if(patrolStuckSince==DateTime.MinValue) patrolStuckSince=now;
            if((now-patrolStuckSince).TotalSeconds>=stuckSeconds)
                return SkipStuckPatrol($"{stuckSeconds}秒間 3m以内", $"{stuckSeconds}秒以上ほぼ同じ位置");
        }

        // A→B→A の往復や同じ周辺をぐるぐるするケースも見る。
        // 0.5秒ごとの位置だけを保持し、設定秒数以上で移動量はあるのに
        // 始点付近へ戻っている場合を「前進していない」と判定する。
        if(patrolLastMotionSampleAt==DateTime.MinValue || (now-patrolLastMotionSampleAt).TotalMilliseconds>=500)
        {
            patrolMotionSamples.Enqueue((now,playerPosition));
            patrolLastMotionSampleAt=now;
        }
        while(patrolMotionSamples.Count>0 && (now-patrolMotionSamples.Peek().At).TotalSeconds>stuckSeconds+2.0)
            patrolMotionSamples.Dequeue();

        if(patrolMotionSamples.Count>=3)
        {
            var samples=patrolMotionSamples.ToArray();
            var span=(samples[^1].At-samples[0].At).TotalSeconds;
            if(span>=stuckSeconds)
            {
                var travelled=0f;
                for(var i=1;i<samples.Length;i++) travelled+=Flat(samples[i-1].Position,samples[i].Position);
                var direct=Flat(samples[0].Position,samples[^1].Position);
                if(travelled>=12f && direct<=5f)
                    return SkipStuckPatrol($"{stuckSeconds}秒間の移動量 {travelled:0.0}m / 実前進 {direct:0.0}m", "同じ周辺の往復・ループ");
            }
        }

        return false;
    }

    private bool SkipStuckPatrol(string reason, string statusReason)
    {
        var c=currentPatrolCandidate!;
        log.Warning("[AMH] 巡回移動停滞を検知して自動スキップ / {Mob} index={Index} MAP={X:0.0},{Y:0.0} / {Reason}",
            Display(c.Mob),c.Index+1,c.Point.X,c.Point.Y,reason);
        Status=$"{statusReason}を検知したため、現在の巡回座標を自動スキップします";
        ResetPatrolStuckWatch();
        SkipCurrentPatrolPoint(statusReason,true);
        return true;
    }

    private void ResetPatrolStuckWatch()
    {
        patrolStuckAnchor=null;
        patrolStuckSince=DateTime.MinValue;
        patrolMotionSamples.Clear();
        patrolLastMotionSampleAt=DateTime.MinValue;
    }

    private List<PatrolCandidate> FilterCurrentPatrolArea(HuntZoneInfo zone, List<PatrolCandidate> candidates)
    {
        if(zone.TerritoryId!=YanxiaTerritory) return candidates;
        return candidates.Where(c=>YanxiaGroupFor(c.Point)==yanxiaPatrolGroup).ToList();
    }

    private bool TryAdvanceYanxiaArea(HuntZoneInfo zone, List<PatrolCandidate> allCandidates)
    {
        if(zone.TerritoryId!=YanxiaTerritory || yanxiaPatrolGroup!=0) return false;
        var nextExists=allCandidates.Any(c=>YanxiaGroupFor(c.Point)==1 && !visitedPatrolPoints.Contains(c.Key));
        if(!nextExists) return false;

        game.StopOwnedVnav();
        ClearFlag();
        routeDestination=null; currentPatrolCandidate=null;
        yanxiaPatrolGroup=1;
        yanxiaNeedTeleport=true;
        yanxiaSawBetweenAreas=false;
        yanxiaStopRequested=false;
        yanxiaTeleportStartPosition=null;
        ResetTravel();
        Set(HunterState.Travelling,"ヤンサ ナマイ村側へ巡回エリアを切り替えます");
        log.Information("[AMH] ヤンサ巡回エリア切替 / 次=ナマイ村側 / テレポ=ナマイ村");
        return true;
    }

    private sealed record PatrolCandidate((string Mob,int Index) Key, string Mob, int Index, (float X,float Y) Point, Vector3 World);

    private List<PatrolCandidate> BuildActivePatrolCandidates(HuntZoneInfo zone)
    {
        var list=new List<PatrolCandidate>();
        foreach(var mob in zone.Mobs)
        {
            if(!Searchable(mob.CanonicalName) || mob.RoutePoints.Length==0) continue;
            for(int i=0;i<mob.RoutePoints.Length;i++)
            {
                var p=mob.RoutePoints[i];
                var world=MapPointToFlagWorld(zone,p);
                if(world.HasValue) list.Add(new PatrolCandidate((mob.CanonicalName,i),mob.CanonicalName,i,p,world.Value));
            }
        }
        return list;
    }

    private void SelectNextPatrolPoint(HuntZoneInfo zone, Vector3 playerPosition, List<PatrolCandidate> remaining)
    {
        PatrolCandidate next;
        if(config.PatrolMode=="Mob")
        {
            // MAP登録順の最初の未完了モブを最後まで回し、その後に次のモブへ進む。
            var mobOrder=zone.Mobs.Select((m,i)=>(m.CanonicalName,i)).ToDictionary(x=>x.CanonicalName,x=>x.i,StringComparer.OrdinalIgnoreCase);
            next=remaining.OrderBy(c=>mobOrder.TryGetValue(c.Mob,out var mi)?mi:int.MaxValue)
                          .ThenBy(c=>c.Index)
                          .First();
        }
        else
        {
            // 全未確認候補をモブ別のまま保持し、現在地から一番近い候補を選ぶ。
            next=remaining.OrderBy(c=>Flat(playerPosition,c.World)).First();
        }

        currentPatrolCandidate=next;
        NotFound.Remove(next.Mob);
        routeDestination=UpdateFlag(zone,next.Point); routeStarted=DateTime.UtcNow;
        if(routeDestination==null) Status=$"巡回地点 {next.Point.X:0.0}, {next.Point.Y:0.0} の旗を設定できません";
    }

    private bool CurrentCandidateMatches(PatrolCandidate c)
        => currentPatrolCandidate is { } active && c.Key==active.Key;

    private int VisitedCountForMob(string mob)
        => visitedPatrolPoints.Count(x=>x.Mob.Equals(mob,StringComparison.OrdinalIgnoreCase));

    private void UpdateProgressForVisitedPoint(string mob)
    {
        Progress[mob]=VisitedCountForMob(mob);
    }

    private static (int X,int Y) PatrolKey((float X,float Y) p)
        => ((int)MathF.Round(p.X*1000f),(int)MathF.Round(p.Y*1000f));
    private static bool SamePoint((float X,float Y) a,(float X,float Y) b)
        => PatrolKey(a)==PatrolKey(b);

    private void BeginTarget(string canonical, IGameObject obj, bool child)
    {
        PlayFoundSound();
        game.StopOwnedVnav(); game.SetCombat(false); currentMob=canonical; currentTargetPosition=obj.Position; targetMissing=null;
        if(config.HuntMode=="Search")
        {
            Set(HunterState.ApproachingTarget,$"{Display(canonical)}を発見・確認距離まで接近中");
            Print($"{Display(canonical)}を発見しました。探索モードのため安全距離まで接近します。");
            return;
        }
        Set(HunterState.ApproachingTarget,$"{Display(canonical)}を発見・接近中");
    }

    private void TickTargetApproach(bool child)
    {
        if(currentMob==null){ ReturnAfterTarget(child,false); return; }
        var mob=FindByCanonical(currentMob);
        if(mob==null)
        {
            targetMissing??=DateTime.UtcNow;
            if((DateTime.UtcNow-targetMissing.Value).TotalSeconds>=3) ReturnAfterTarget(child,false);
            return;
        }
        targetMissing=null; currentTargetPosition=mob.Position;
        game.SetCombat(false);
        var player=objects.LocalPlayer; if(player==null) return;
        var d=Flat(player.Position,mob.Position);

        // 探索モードは発見したAモブ本人の実座標へ飛行接近し、
        // 約30mまで寄ったところで停止する。着地・ターゲット・戦闘は行わない。
        if(config.HuntMode=="Search")
        {
            if(d>30f)
            {
                if(!EnsureMountedAndFlying()) return;
                var searchApproach=new Vector3(mob.Position.X,MathF.Max(player.Position.Y,mob.Position.Y+15f),mob.Position.Z);
                if(!game.IsBusy()) game.Move(searchApproach,true);
                Status=$"{Display(currentMob)}へ確認距離まで接近中 ({d:0.0}m)";
                return;
            }

            game.StopOwnedVnav();
            Found.Add(currentMob);
            Progress[currentMob]=GetMob(currentMob)?.RoutePoints.Length??0;
            ClearFlag();
            Active=false;
            Set(HunterState.Found,$"{Display(currentMob)} 発見済み・約{d:0.0}mで停止");
            Print($"{Display(currentMob)}へ約{d:0.0}mまで接近して停止しました。");
            return;
        }

        if(d>15f)
        {
            if(!EnsureMountedAndFlying()) return;
            var approach=new Vector3(mob.Position.X,MathF.Max(player.Position.Y,mob.Position.Y+12f),mob.Position.Z);
            if(!game.IsBusy()) game.Move(approach,true);
            Status=$"{Display(currentMob)}へ飛行接近中 ({d:0.0}m)";
            return;
        }
        game.StopOwnedVnav();
        if(!mob.IsTargetable)
        {
            if(!EnsureMountedAndFlying()) return;
            var approach=new Vector3(mob.Position.X,MathF.Max(player.Position.Y,mob.Position.Y+12f),mob.Position.Z);
            if(!game.IsBusy()) game.Move(approach,true);
            Status=$"{Display(currentMob)}へターゲット可能距離まで接近中 ({d:0.0}m)";
            return;
        }
        targets.Target=mob; ownsTarget=true;
        ResetLandingRecovery();
        landingAttemptStarted=DateTime.UtcNow;
        Set(HunterState.Landing,$"{Display(currentMob)}へ着地中");
    }

    private void TickLanding(bool child)
    {
        if(currentMob==null){ ReturnAfterTarget(child,false); return; }
        var mob=FindByCanonical(currentMob); if(mob==null){ targetMissing??=DateTime.UtcNow; if((DateTime.UtcNow-targetMissing.Value).TotalSeconds>=3) ReturnAfterTarget(child,false); return; }
        targetMissing=null; game.SetCombat(false);
        if(!mob.IsTargetable){ ResetLandingRecovery(); Set(HunterState.ApproachingTarget,$"{Display(currentMob)}へ再接近中"); return; }
        targets.Target=mob; ownsTarget=true;

        // 着地失敗時は同じ場所で降車要求を連打し続けない。
        // いったんAモブ周辺の地面へ飛行移動し直してから、再度着地を試す。
        if(landingRecoveryDestination is { } recovery)
        {
            var player=objects.LocalPlayer;
            if(player==null) return;
            var reached=Flat(player.Position,recovery)<=4f;
            var timedOut=(DateTime.UtcNow-landingRecoveryStarted).TotalSeconds>=5.0;
            if(!game.IsFlying || reached || timedOut)
            {
                game.StopOwnedVnav();
                landingRecoveryDestination=null;
                landingRecoveryStarted=DateTime.MinValue;
                landingAttemptStarted=DateTime.UtcNow;
                nextAction=DateTime.MinValue;
            }
            else
            {
                Status=$"{Display(currentMob)}：着地点を調整中 ({landingRetryCount})";
                return;
            }
        }

        game.StopOwnedVnav();
        if(game.IsFlying)
        {
            if(landingAttemptStarted==DateTime.MinValue) landingAttemptStarted=DateTime.UtcNow;
            if(DateTime.UtcNow>=nextAction)
            {
                game.ToggleMount();
                nextAction=DateTime.UtcNow.AddSeconds(1);
            }

            if((DateTime.UtcNow-landingAttemptStarted).TotalSeconds>=3.0)
            {
                landingRetryCount++;
                var landingPoint=FindLandingRecoveryPoint(mob.Position,landingRetryCount);
                if(landingPoint is { } point)
                {
                    log.Warning("[AMH] 着地できないため着地点を変更 / Mob={Mob} / Retry={Retry} / Dest=({X:0.0},{Y:0.0},{Z:0.0})",
                        Display(currentMob),landingRetryCount,point.X,point.Y,point.Z);
                    if(game.Move(point,true))
                    {
                        landingRecoveryDestination=point;
                        landingRecoveryStarted=DateTime.UtcNow;
                        landingAttemptStarted=DateTime.MinValue;
                        Status=landingRetryCount<=3
                            ? $"{Display(currentMob)}：再接近して着地を再試行 ({landingRetryCount}/3)"
                            : $"{Display(currentMob)}：少し離れた着地点を探索中";
                        return;
                    }
                }
                // 地面候補が取れなかった場合も3秒ごとに再探索する。
                landingAttemptStarted=DateTime.UtcNow;
            }
            else
            {
                Status=$"{Display(currentMob)}へ着地中";
            }
            return;
        }

        // InFlight=falseになってからのみマウント解除する。
        landingAttemptStarted=DateTime.MinValue;
        landingRecoveryDestination=null;
        if(game.IsMounted){ if(DateTime.UtcNow>=nextAction){ game.ToggleMount(); nextAction=DateTime.UtcNow.AddSeconds(1); } Status=$"{Display(currentMob)}へ降車中"; return; }
        ResetLandingRecovery();
        if(config.SummonChocoboBeforeCombat)
        {
            chocoboSummonAttempted=false;
            chocoboSummonAttempts=0;
            chocoboSummonRequestAt=DateTime.MinValue;
            Set(HunterState.PreparingCompanion,$"{Display(currentMob)}：チョコボ確認中");
            return;
        }
        Set(HunterState.Combat,$"{Display(currentMob)}と戦闘中"); game.SetCombat(true);
    }

    private Vector3? FindLandingRecoveryPoint(Vector3 mobPosition, int retry)
    {
        // 最初の3回はAモブのすぐ近く。4回目以降は少しずつ外側の地面を探す。
        var radius = retry switch { 1 => 0f, 2 => 5f, 3 => 7f, _ => MathF.Min(18f, 10f + (retry-4)*2f) };
        var startAngle = retry * (MathF.PI / 3f);
        var samples = radius<=0f ? 1 : 8;
        Vector3? best=null;
        var player=objects.LocalPlayer;
        var bestDist=float.MaxValue;

        for(var i=0;i<samples;i++)
        {
            var angle=startAngle + i*(MathF.PI*2f/samples);
            var probe=new Vector3(mobPosition.X + MathF.Cos(angle)*radius, mobPosition.Y + 4f, mobPosition.Z + MathF.Sin(angle)*radius);
            var floor=game.PointOnFloor(probe);
            if(floor is not { } f) continue;
            var candidate=new Vector3(f.X,f.Y+3f,f.Z);
            var dist=player==null ? radius : Flat(player.Position,candidate);
            if(dist<bestDist){ bestDist=dist; best=candidate; }
        }
        return best;
    }

    private void ResetLandingRecovery()
    {
        landingRetryCount=0;
        landingAttemptStarted=DateTime.MinValue;
        landingRecoveryDestination=null;
        landingRecoveryStarted=DateTime.MinValue;
    }

    private void TickCompanion(bool child)
    {
        if(currentMob==null){ ReturnAfterTarget(child,false); return; }
        if(game.IsFlying||game.IsMounted){ Set(HunterState.Landing,$"{Display(currentMob)}へ再着地中"); return; }

        var left=game.ChocoboTimeLeft();
        if(left>=300f)
        {
            if(chocoboSummonAttempted)
                log.Information("[AMH] チョコボ呼び出し確認 / 残り={Seconds:0}秒 / 試行={Attempts}", left, chocoboSummonAttempts);
            Set(HunterState.Combat,$"{Display(currentMob)}と戦闘中");
            game.SetCombat(true);
            return;
        }

        // FATEのSummonChocobo()を参考に、ギサール使用後は3秒待って
        // CompanionInfo.TimeLeftが実際に増えたことを確認する。
        if(!chocoboSummonAttempted)
        {
            chocoboSummonAttempted=true;
            chocoboSummonAttempts=1;
            chocoboSummonRequestAt=DateTime.UtcNow;
            log.Information("[AMH] ギサールの野菜使用要求 / チョコボ残り={Seconds:0}秒 / 試行=1", left);
            game.UseGysahlGreens();
            Status=$"{Display(currentMob)}：チョコボを呼び出しています（残り{Math.Max(0,left)/60f:0.0}分）";
            return;
        }

        if((DateTime.UtcNow-chocoboSummonRequestAt).TotalSeconds<3.0)
        {
            Status=$"{Display(currentMob)}：チョコボ呼び出し確認待ち";
            return;
        }

        left=game.ChocoboTimeLeft();
        if(left>=300f)
        {
            log.Information("[AMH] チョコボ呼び出し成功 / 残り={Seconds:0}秒 / 試行={Attempts}", left, chocoboSummonAttempts);
            Set(HunterState.Combat,$"{Display(currentMob)}と戦闘中");
            game.SetCombat(true);
            return;
        }

        if(chocoboSummonAttempts<3)
        {
            chocoboSummonAttempts++;
            chocoboSummonRequestAt=DateTime.UtcNow;
            log.Warning("[AMH] チョコボ残り時間が増えていないためギサール再試行 / 残り={Seconds:0}秒 / 試行={Attempts}", left, chocoboSummonAttempts);
            game.UseGysahlGreens();
            Status=$"{Display(currentMob)}：ギサールの野菜を再試行中 ({chocoboSummonAttempts}/3)";
            return;
        }

        log.Warning("[AMH] チョコボ呼び出しを3回試行しましたが確認できません。戦闘を継続します / 残り={Seconds:0}秒", left);
        Set(HunterState.Combat,$"{Display(currentMob)}と戦闘中（チョコボ呼び出し未確認）");
        game.SetCombat(true);
    }

    private void TickCombat(bool child)
    {
        if(currentMob==null){ ReturnAfterTarget(child,false); return; }
        if((DateTime.UtcNow-stateSince).TotalMinutes>=10){ Print("戦闘が10分を超えたため停止します。"); StopOwned(true); return; }

        // FATE側と同じ考え方：戦闘フェーズへ入った後はAMHが移動・再位置調整・
        // ターゲット切替をしない。RSR等の戦闘側へ完全に任せる。
        // ここでは戦闘状態と討伐完了だけを監視する。
        game.ForceClearOwnedVnavFlag();
        game.SetCombat(true);

        var mob=FindByCanonical(currentMob);
        if(mob!=null && !mob.IsDead)
        {
            targetMissing=null;
            Status=$"{Display(currentMob)}と戦闘中（移動・回避は戦闘プラグインへ委譲）";
            return;
        }

        // Aモブ本体が倒れた後もヘイトが残っている間は、AMHからは何も操作せず
        // 戦闘プラグイン側の残敵処理が終わるのを待つ。
        var hateIds=game.HateEntityIds();
        if(hateIds.Length>0 || game.IsInCombat)
        {
            targetMissing=null;
            Status=$"{Display(currentMob)}討伐後：戦闘終了待ち（ヘイト {hateIds.Length}体）";
            return;
        }

        ReturnAfterTarget(child,true);
    }

    private IGameObject? FindHateTarget(uint[] hateIds, Vector3 playerPosition)
    {
        if(hateIds.Length==0) return null;
        var ids=hateIds.ToHashSet();
        return objects.Where(o => o!=null && ids.Contains(o.EntityId) && !o.IsDead && o.IsTargetable)
            .OrderBy(o => Flat(playerPosition,o.Position)).FirstOrDefault();
    }

    private static float DesiredCombatDistance(uint jobId)
    {
        // タンク・近接は4m、遠隔・キャスター・ヒーラーは20m。対象のHitboxRadiusは呼び出し側で加算する。
        return jobId switch
        {
            1 or 2 or 3 or 4 or 19 or 20 or 21 or 22 or 29 or 30 or 32 or 34 or 37 or 39 or 41 => 4f,
            _ => 20f,
        };
    }

    private void ReturnAfterTarget(bool child, bool defeated)
    {
        game.StopOwnedVnav(); game.SetCombat(false); if(ownsTarget){ try{targets.Target=null;}catch{} ownsTarget=false; }
        if(defeated && currentMob!=null){ RecordHuntResult(currentMob,currentTargetPosition); Completed.Add(currentMob); Print($"{Display(currentMob)}の討伐を確認しました。"); }
        currentMob=null; currentTargetPosition=null; patrolMob=null; routeIndex=0; routeDestination=null; targetMissing=null; chocoboSummonAttempted=false; chocoboSummonAttempts=0; chocoboSummonRequestAt=DateTime.MinValue;
        ResetLandingRecovery();
        currentPatrolCandidate=null;
        ClearFlag();
        Set(HunterState.Remounting,"再騎乗中");
    }

    private void TickRemount(bool child)
    {
        if(!game.IsMounted){ if(DateTime.UtcNow>=nextAction){ game.ToggleMount(); nextAction=DateTime.UtcNow.AddSeconds(2); } Status="再騎乗中"; return; }
        if(!game.IsFlying){ if(DateTime.UtcNow>=nextAction){ game.TakeOff(); nextAction=DateTime.UtcNow.AddSeconds(1.5); } Status="再離陸中"; return; }
        if(child) Set(HunterState.ChildWaiting,"親機から次のAモブ情報を待っています"); else Set(HunterState.Patrol,"巡回再開");
    }

    private bool EnsureMountedAndFlying()
    {
        // 潜水中は InFlight=False になる。ここで離陸(GeneralAction 2)を再発行すると
        // 水中から出る→再び潜るを繰り返すため、潜水状態はそのまま移動可能として扱う。
        if(game.IsDiving) return true;
        if(!game.IsMounted){ if(DateTime.UtcNow>=nextAction){ game.StopOwnedVnav(); game.ToggleMount(); nextAction=DateTime.UtcNow.AddSeconds(2); } Status="騎乗中"; return false; }
        if(!game.IsFlying){ if(DateTime.UtcNow>=nextAction){ game.StopOwnedVnav(); game.TakeOff(); nextAction=DateTime.UtcNow.AddSeconds(1.5); } Status="離陸中"; return false; }
        return true;
    }

    private (IGameObject? Object,string? Canonical) FindAnyTarget()
    {
        IGameObject? best=null; string? canon=null; float bd=float.MaxValue;
        var player=objects.LocalPlayer; if(player==null)return(null,null);

        // 発見判定は「攻撃可能な敵か」ではなく、まず登録済みAモブ本人が
        // ObjectTable上に存在するかで行う。Hostile / ICharacter / IsTargetable は
        // 遠距離のAモブを落とす原因になるため、巡回中の発見条件には使わない。
        foreach(var o in objects)
        {
            var c=Canonical(o.Name.TextValue);
            if(c==null||Completed.Contains(c)||IsExcluded(c)||o.IsDead)continue;
            if(config.HuntMode=="Search" && Found.Contains(c)) continue;
            var d=Flat(player.Position,o.Position);
            if(d>=bd)continue;
            best=o; canon=c; bd=d;
        }

        if(best!=null&&canon!=null)
        {
            var hostile=best is ICharacter ch&&ch.StatusFlags.HasFlag(StatusFlags.Hostile);
            log.Information("[AMH][Detect] {Mob} found dist={Distance:0.0} kind={Kind} targetable={Targetable} hostile={Hostile} dead={Dead} pos=({X:0.00},{Y:0.00},{Z:0.00})",
                Display(canon),bd,best.ObjectKind,best.IsTargetable,hostile,best.IsDead,best.Position.X,best.Position.Y,best.Position.Z);
        }
        return(best,canon);
    }

    private IGameObject? FindByCanonical(string canonical)
    {
        IGameObject? best=null; float bd=float.MaxValue; var player=objects.LocalPlayer; if(player==null)return null;
        foreach(var o in objects)
        {
            if(o.IsDead||!string.Equals(Canonical(o.Name.TextValue),canonical,StringComparison.OrdinalIgnoreCase))continue;
            var d=Flat(player.Position,o.Position);
            if(d<bd){best=o;bd=d;}
        }
        return best;
    }

    private bool Searchable(string c)=>!IsExcluded(c)&&!Completed.Contains(c)&&!(config.HuntMode=="Search"&&Found.Contains(c));
    public bool IsExcluded(string c)=>config.ExcludedMobs.Any(x=>x.Equals(c,StringComparison.OrdinalIgnoreCase));
    public void SetExcluded(string c,bool v){ config.ExcludedMobs.RemoveAll(x=>x.Equals(c,StringComparison.OrdinalIgnoreCase)); if(v)config.ExcludedMobs.Add(c); }
    public bool IsMapExcluded(uint territoryId)=>config.ExcludedTerritories.Contains(territoryId);
    public void SetMapExcluded(uint territoryId,bool v){ config.ExcludedTerritories.RemoveAll(x=>x==territoryId); if(v) config.ExcludedTerritories.Add(territoryId); }
    private bool IsKnown(string c)=>Zone?.Mobs.Any(x=>x.CanonicalName.Equals(c,StringComparison.OrdinalIgnoreCase))==true;
    private HuntMobInfo? GetMob(string c)=>Zone?.Mobs.FirstOrDefault(x=>x.CanonicalName.Equals(c,StringComparison.OrdinalIgnoreCase));
    private string Display(string c)=>GetMob(c)?.DisplayName??c;
    private string? Canonical(string n){ foreach(var m in Zone?.Mobs??[]){ if(n.Equals(m.CanonicalName,StringComparison.OrdinalIgnoreCase)||n.Equals(m.DisplayName,StringComparison.OrdinalIgnoreCase))return m.CanonicalName; } return null; }

    private static float Flat(Vector3 a,Vector3 b){var dx=a.X-b.X;var dz=a.Z-b.Z;return MathF.Sqrt(dx*dx+dz*dz);}

    private string? DirectAetheryte(uint territory)
    {
        try{ foreach(var a in data.GetExcelSheet<Aetheryte>()){ if(!a.IsAetheryte||a.Territory.RowId!=territory)continue; var n=a.PlaceName.Value.Name.ExtractText(); if(!string.IsNullOrWhiteSpace(n))return n; } }catch(Exception ex){log.Debug(ex,"[AMH] aetheryte");} return null;
    }
    private string CurrentWorldName(){ try{return playerState.CurrentWorld.Value.Name.ExtractText();}catch{return "";} }
    private void ResetTravel(string step=""){travelStep=step;travelCommandSent=false;travelStarted=DateTime.MinValue;}
    private static string StatusKey(string text)
    {
        if(string.IsNullOrWhiteSpace(text)) return "";
        var i=text.IndexOf(" (",StringComparison.Ordinal);
        if(i>0) text=text[..i];
        i=text.IndexOf('（');
        if(i>0) text=text[..i];
        return text.Trim();
    }
    private void Set(HunterState s,string text){State=s;Status=text;stateSince=DateTime.UtcNow;}
    private void Print(string t)=>chat.Print($"[A-Rank Hunt Tour Assistant] {t}");

    private Vector3? MapPointToFlagWorld(HuntZoneInfo zone, (float X,float Y) mapPoint)
    {
        try
        {
            var territoryRow=data.GetExcelSheet<TerritoryType>().GetRowOrDefault(zone.TerritoryId);
            var mapId=territoryRow?.Map.RowId ?? 0u;
            if(mapId==0) return null;
            var payload=new MapLinkPayload(zone.TerritoryId,mapId,mapPoint.X,mapPoint.Y);
            return new Vector3(payload.RawX/1000f,0f,payload.RawY/1000f);
        }
        catch(Exception ex){ log.Debug(ex,"[AMH] MAP座標の旗座標変換に失敗"); return null; }
    }

    private unsafe Vector3? UpdateFlag(HuntZoneInfo zone, (float X, float Y) mapPoint)
    {
        if (clientState.TerritoryType != config.SelectedTerritoryId) return null;

        try
        {
            var territoryRow=data.GetExcelSheet<TerritoryType>().GetRowOrDefault(config.SelectedTerritoryId);
            var mapId=territoryRow?.Map.RowId ?? 0u;
            if(mapId==0)
            {
                log.Warning("[AMH] 巡回地点のMAP旗表示を中止 / Territory={Territory} のMapIdを取得できません", config.SelectedTerritoryId);
                return null;
            }

            // DalamudのMapLinkPayloadと同じ変換で、人が見るMAP X/Yからゲーム内部の旗X/Zを作る。
            var converted=MapPointToFlagWorld(zone,mapPoint);
            if(!converted.HasValue) return null;
            var flagPosition=converted.Value;

            var map = AgentMap.Instance();
            if (map == null) return null;

            // 同じ巡回地点でも、ゲーム側で旗が消えていたらキャッシュを信用せず再設定する。
            if (lastPatrolFlagTerritory == config.SelectedTerritoryId &&
                lastPatrolFlagDestination is Vector3 last &&
                Vector3.DistanceSquared(last, flagPosition) < 0.01f &&
                map->FlagMarkerCount > 0)
                return last;

            // ゲームの旗を先に立てる。vnavmeshへXYZは渡さず、この旗そのものを使わせる。
            map->SetFlagMapMarker(config.SelectedTerritoryId, mapId, flagPosition);

            if(map->FlagMarkerCount <= 0)
            {
                lastPatrolFlagTerritory = 0;
                lastPatrolFlagDestination = null;
                log.Warning("[AMH] MAP旗設定後もFlagMarkerCount=0 / Territory={Territory} MapId={MapId} / MAP={MapX:0.0},{MapY:0.0}。次Tickで再試行します",
                    config.SelectedTerritoryId, mapId, mapPoint.X, mapPoint.Y);
                return null;
            }

            lastPatrolFlagTerritory = config.SelectedTerritoryId;
            lastPatrolFlagDestination = flagPosition;

            log.Information("[AMH] 次の巡回地点へMAP旗設定・確認OK / Territory={Territory} MapId={MapId} / MAP={MapX:0.0},{MapY:0.0} / FlagWorld=<{X:0.000},{Z:0.000}> / FlagCount={FlagCount}",
                config.SelectedTerritoryId, mapId, mapPoint.X, mapPoint.Y, flagPosition.X, flagPosition.Z, map->FlagMarkerCount);
            return flagPosition;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[AMH] 巡回地点のMAP旗表示に失敗");
            return null;
        }
    }


    private unsafe bool PatrolFlagPresent()
    {
        try
        {
            var map=AgentMap.Instance();
            return map!=null && map->FlagMarkerCount>0;
        }
        catch(Exception ex)
        {
            log.Debug(ex,"[AMH] MAP旗確認に失敗");
            return false;
        }
    }

    private unsafe void ClearFlag()
    {
        try
        {
            var map = AgentMap.Instance();
            if (map != null) map->FlagMarkerCount = 0;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[AMH] 巡回地点のMAP旗消去に失敗");
        }
        lastPatrolFlagDestination = null;
        lastPatrolFlagTerritory = 0;
    }
}
