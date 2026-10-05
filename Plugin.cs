using System.Numerics;
using System.Diagnostics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace ARankHuntTourAssistant;

public sealed class Plugin : IDalamudPlugin
{
    private const string VersionText="0.1.1";
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly GameActions game;
    private readonly LanBridge lan;
    private readonly AutomationController automation;
    private readonly HuntRecordStore records;
    private bool open=true;
    private string recordOutputDirectory;

    private enum InstancePatrolStage
    {
        Idle,
        MoveToAetheryte,
        WaitingForChange,
        Changing
    }

    private InstancePatrolStage instanceStage=InstancePatrolStage.Idle;
    private readonly HashSet<int> visitedInstances=new();
    private int targetInstance;
    private int knownInstanceCount;
    private DateTime instanceStageSince=DateTime.MinValue;
    private DateTime instanceTeleportRequestedAt=DateTime.MinValue;
    private bool instanceTeleportRequested;
    private bool instanceCycleDone;
    private string instanceStatus="";

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework, IObjectTable objects,
        IClientState clientState, IPlayerState playerState, ICondition condition,
        ITargetManager targets, IChatGui chat, IDataManager data, IPluginLog log)
    {
        this.pi=pi; this.commands=commands; this.framework=framework; this.clientState=clientState; this.data=data; this.log=log;
        config=pi.GetPluginConfig() as Configuration ?? new Configuration();
        config.Role="Solo"; config.HuntMode="Search"; config.AlwaysRecordHunts=true;
        if(!HuntData.Expansions.Any(x=>x.Value==config.SelectedExpansion)){ config.SelectedExpansion="ShB"; config.SelectedTerritoryId=813; }
        if(!HuntData.DataCenters.Contains(config.SelectedDataCenter)) config.SelectedDataCenter="Meteor";
        if(config.SelectedWorld!="現在のワールド" && !HuntData.GetWorlds(config.SelectedDataCenter).Contains(config.SelectedWorld)) config.SelectedWorld="現在のワールド";
        recordOutputDirectory=config.RecordOutputDirectory;
        game=new GameActions(pi,commands,condition,log);
        lan=new LanBridge(log);
        records=new HuntRecordStore(pi.ConfigDirectory.FullName,config.RecordOutputDirectory,()=> (int)clientState.Instance);
        if(string.IsNullOrWhiteSpace(recordOutputDirectory)) recordOutputDirectory=records.DirectoryPath;
        automation=new AutomationController(config,game,lan,objects,clientState,playerState,targets,chat,data,log,records);
        lan.Start(config);
        commands.AddHandler("/arankhunttour",new CommandInfo((_,_)=>open=true){HelpMessage="Aモブハントツアー補助ツールを開きます。"});
        framework.Update += OnUpdate;
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenConfigUi += Open;
        pi.UiBuilder.OpenMainUi += Open;
        log.Information("[AMH] A-Rank Hunt Tour Assistant v{Version} loaded",VersionText);
    }

    private void OnUpdate(IFramework _)
    {
        if(instanceStage!=InstancePatrolStage.Idle)
        {
            TickInstancePatrol();
            return;
        }

        automation.Tick();

        if(!instanceCycleDone && config.PatrolAllInstances && !config.PatrolExpansionMaps && !config.PatrolWorlds &&
           !automation.Active && automation.State==HunterState.Complete)
        {
            BeginInstanceAdvance();
        }
    }

    private void Open()=>open=true;
    private void Save()
    {
        config.Role="Solo"; config.HuntMode="Search"; config.AlwaysRecordHunts=true;
        config.RecordOutputDirectory=recordOutputDirectory.Trim();
        try { recordOutputDirectory=automation.SetRecordDirectory(config.RecordOutputDirectory); config.RecordOutputDirectory=recordOutputDirectory; } catch { }
        pi.SavePluginConfig(config); lan.Start(config);
    }

    private void Start()
    {
        Save();
        ResetInstanceCycle();
        var current=(int)clientState.Instance;
        if(current>0) visitedInstances.Add(current);
        automation.Start();
    }

    private void Stop()
    {
        ResetInstanceCycle();
        automation.Stop();
    }

    private void ResetInstanceCycle()
    {
        instanceStage=InstancePatrolStage.Idle;
        visitedInstances.Clear();
        targetInstance=0;
        knownInstanceCount=0;
        instanceStageSince=DateTime.MinValue;
        instanceTeleportRequestedAt=DateTime.MinValue;
        instanceTeleportRequested=false;
        instanceCycleDone=false;
        instanceStatus="";
    }

    private void BeginInstanceAdvance()
    {
        var current=(int)clientState.Instance;
        if(current<=0)
        {
            instanceCycleDone=true;
            instanceStatus="このMAPは現在複数インスタンスではありません";
            log.Information("[ARHTA][Instance] 現在のインスタンス番号が0のため追加巡回なし / Territory={Territory}",config.SelectedTerritoryId);
            return;
        }

        if(!game.IsLifestreamAvailable)
        {
            instanceCycleDone=true;
            instanceStatus="Lifestream IPCを取得できないため、インスタンス巡回を終了しました";
            log.Warning("[ARHTA][Instance] Lifestream IPCを利用できません");
            return;
        }

        visitedInstances.Add(current);
        instanceStage=InstancePatrolStage.MoveToAetheryte;
        instanceStageSince=DateTime.UtcNow;
        instanceTeleportRequested=false;
        instanceTeleportRequestedAt=DateTime.MinValue;
        instanceStatus="次のインスタンス確認のためエーテライトへ移動します";
        game.ForceStopVnav();
        log.Information("[ARHTA][Instance] MAP巡回完了 / Current={Current} / Visited={Visited}",current,string.Join(',',visitedInstances.OrderBy(x=>x)));
    }

    private void TickInstancePatrol()
    {
        if(!config.PatrolAllInstances || config.PatrolExpansionMaps || config.PatrolWorlds)
        {
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus="";
            return;
        }

        if(!game.IsLifestreamAvailable)
        {
            log.Warning("[ARHTA][Instance] 切替処理中にLifestream IPCを利用できなくなったため安全停止");
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus="Lifestreamを利用できなくなったためインスタンス巡回を停止しました";
            return;
        }

        if((DateTime.UtcNow-instanceStageSince).TotalSeconds>150)
        {
            log.Warning("[ARHTA][Instance] インスタンス切替が150秒以内に完了しなかったため終了 / Stage={Stage}",instanceStage);
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus="インスタンス切替がタイムアウトしました。/xllogを確認してください";
            return;
        }

        switch(instanceStage)
        {
            case InstancePatrolStage.MoveToAetheryte:
                TickMoveToAetheryteForInstance();
                break;
            case InstancePatrolStage.WaitingForChange:
                TickChooseNextInstance();
                break;
            case InstancePatrolStage.Changing:
                TickWaitInstanceChanged();
                break;
        }
    }

    private void TickMoveToAetheryteForInstance()
    {
        if(game.IsBetweenAreas || game.IsLifestreamBusy())
        {
            instanceStatus="エーテライト移動完了待ち";
            return;
        }

        if(game.CanChangeInstance())
        {
            instanceStage=InstancePatrolStage.WaitingForChange;
            instanceStageSince=DateTime.UtcNow;
            instanceStatus="インスタンス数を確認中";
            return;
        }

        var aetheryte=DirectAetheryte(config.SelectedTerritoryId);
        if(string.IsNullOrWhiteSpace(aetheryte))
        {
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus="このMAPのエーテライトを取得できないためインスタンス巡回を終了しました";
            log.Warning("[ARHTA][Instance] エーテライトを取得できません / Territory={Territory}",config.SelectedTerritoryId);
            return;
        }

        if(!instanceTeleportRequested || (DateTime.UtcNow-instanceTeleportRequestedAt).TotalSeconds>45)
        {
            game.ForceStopVnav();
            game.Teleport(aetheryte);
            instanceTeleportRequested=true;
            instanceTeleportRequestedAt=DateTime.UtcNow;
            instanceStatus=$"{aetheryte}へ移動中（インスタンス切替準備）";
            log.Information("[ARHTA][Instance] インスタンス切替準備でエーテライトへテレポ / {Aetheryte}",aetheryte);
        }
        else
        {
            instanceStatus=$"{aetheryte}でインスタンス切替可能になるのを待っています";
        }
    }

    private void TickChooseNextInstance()
    {
        if(game.IsBetweenAreas || game.IsLifestreamBusy()) return;
        if(!game.CanChangeInstance())
        {
            instanceStage=InstancePatrolStage.MoveToAetheryte;
            instanceStageSince=DateTime.UtcNow;
            instanceTeleportRequested=false;
            return;
        }

        var current=(int)clientState.Instance;
        if(current>0) visitedInstances.Add(current);
        knownInstanceCount=game.NumberOfInstances();

        // Lifestreamのインスタンス数は、対象MAPでインスタンス選択メニューを
        // 一度読み取るまで0のことがある。0を「1個」と推測せず、短時間待ってから安全停止する。
        if(knownInstanceCount==0)
        {
            if((DateTime.UtcNow-instanceStageSince).TotalSeconds<10)
            {
                instanceStatus="Lifestreamからインスタンス数を取得中";
                return;
            }

            log.Warning("[ARHTA][Instance] Lifestreamがインスタンス数を未取得 / Territory={Territory} Current={Current}",config.SelectedTerritoryId,current);
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus="Lifestreamがインスタンス数を未取得です。エーテライトのインスタンス選択を一度開いてから再実行してください";
            return;
        }

        if(knownInstanceCount==1)
        {
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus="このMAPはインスタンス1のみです";
            log.Information("[ARHTA][Instance] 追加巡回不要 / Current={Current} Count=1",current);
            return;
        }

        if(knownInstanceCount<current || knownInstanceCount>9)
        {
            log.Warning("[ARHTA][Instance] インスタンス数が現在値と整合しないため安全停止 / Current={Current} Count={Count}",current,knownInstanceCount);
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus="Lifestreamのインスタンス数が現在値と一致しないため停止しました。インスタンス選択を開いて情報を更新してください";
            return;
        }

        targetInstance=Enumerable.Range(1,knownInstanceCount).FirstOrDefault(x=>!visitedInstances.Contains(x));
        if(targetInstance<=0)
        {
            instanceCycleDone=true;
            instanceStage=InstancePatrolStage.Idle;
            instanceStatus=$"全インスタンス巡回完了（{knownInstanceCount}/{knownInstanceCount}）";
            log.Information("[ARHTA][Instance] 全インスタンス巡回完了 / Count={Count}",knownInstanceCount);
            return;
        }

        if(!game.ChangeInstance(targetInstance))
        {
            instanceStatus=$"インスタンス{targetInstance}への移動要求に失敗。再試行待ち";
            return;
        }

        instanceStage=InstancePatrolStage.Changing;
        instanceStageSince=DateTime.UtcNow;
        instanceStatus=$"インスタンス{targetInstance}へ移動中";
    }

    private void TickWaitInstanceChanged()
    {
        if(game.IsBetweenAreas || game.IsLifestreamBusy()) return;

        var current=(int)clientState.Instance;
        if(current!=targetInstance) return;

        visitedInstances.Add(current);
        instanceStage=InstancePatrolStage.Idle;
        instanceStageSince=DateTime.MinValue;
        instanceTeleportRequested=false;
        instanceStatus=$"インスタンス{current}へ移動完了。巡回を開始します";
        log.Information("[ARHTA][Instance] インスタンス移動完了・巡回再開 / Current={Current} / Count={Count}",current,knownInstanceCount);
        automation.Start();
    }

    private string? DirectAetheryte(uint territory)
    {
        try
        {
            foreach(var a in data.GetExcelSheet<Aetheryte>())
            {
                if(!a.IsAetheryte || a.Territory.RowId!=territory) continue;
                var name=a.PlaceName.Value.Name.ExtractText();
                if(!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch(Exception ex)
        {
            log.Debug(ex,"[ARHTA][Instance] エーテライト検索に失敗");
        }
        return null;
    }

    private void PutRecordInChat(HuntRecord record)
    {
        if(!automation.PutRecordInChat(record)) return;
        if(record.Instance<=0) return;
        var text=$"インスタンス{record.Instance} {record.Mob} ({record.MapX:0.0}, {record.MapY:0.0}) <flag>";
        game.SetChatInput(Encoding.UTF8.GetBytes(text));
    }

    private static string InstanceLabel(HuntRecord record)
        => record.Instance>0 ? $" インスタンス{record.Instance}" : "";

    private void Draw()
    {
        if(!open)return;
        ImGui.SetNextWindowSize(new Vector2(620,600),ImGuiCond.FirstUseEver);
        if(!ImGui.Begin($"A-Rank Hunt Tour Assistant v{VersionText}###ARankHuntTourAssistant",ref open,ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse)){ImGui.End();return;}
        ImGui.Text("Aモブハントツアー補助ツール"); ImGui.Separator();
        if(ImGui.BeginTabBar("##amh_tabs"))
        {
            if(ImGui.BeginTabItem("操作"))
            {
                ImGui.BeginChild("##operation_scroll",new Vector2(0,0),false);
        DrawDataCenter(); DrawWorld(); DrawExpansion(); DrawMap();
        var zone=automation.Zone;
        if(zone!=null)
        {
            ImGui.Text("対象Aモブ（チェック＝行かない）");
            foreach(var mob in zone.Mobs)
            {
                bool excluded=automation.IsExcluded(mob.CanonicalName);
                if(ImGui.Checkbox($"##skip_{mob.CanonicalName}",ref excluded)&&!automation.Active) automation.SetExcluded(mob.CanonicalName,excluded);
                ImGui.SameLine();
                string result; Vector4 color;
                if(excluded){result="対象外";color=new(.65f,.65f,.65f,1);}
                else if(automation.Found.Contains(mob.CanonicalName)){result="発見済み";color=new(.8f,.55f,1f,1);}
                else if(automation.NotFound.Contains(mob.CanonicalName)){result="未発見";color=new(1f,.72f,.22f,1);}
                else{result="探索中";color=new(.35f,.8f,1f,1);}
                var p=automation.Progress.TryGetValue(mob.CanonicalName,out var n)?n:0;
                ImGui.TextColored(color,$"{mob.DisplayName}：{result} / 巡回地点 {p}/{mob.RoutePoints.Length}");
            }
        }
        ImGui.Separator();
        var currentInstance=(int)clientState.Instance;
        var numberOfInstances=game.NumberOfInstances();
        if(currentInstance>0)
            ImGui.Text(numberOfInstances>0 ? $"現在のインスタンス：{currentInstance} / {numberOfInstances}" : $"現在のインスタンス：{currentInstance}");
        if(!string.IsNullOrWhiteSpace(instanceStatus)) ImGui.TextWrapped($"インスタンス巡回：{instanceStatus}");
        var playSound=config.PlaySoundOnFound;
        if(ImGui.Checkbox("Aモブ発見時に通知音を鳴らす",ref playSound)&&!automation.Active) config.PlaySoundOnFound=playSound;
        ImGui.Separator();
        ImGui.Text($"状態：{automation.Status}");
        var history=automation.PreviousStatuses;
        if(history.Count>0) ImGui.TextDisabled($"1つ前：{history[0]}");
        if(history.Count>1) ImGui.TextDisabled($"2つ前：{history[1]}");
        var patrolMap = automation.CurrentPatrolMapPoint;
        var patrolInternal = automation.CurrentPatrolInternalPoint;
        if (automation.CurrentPatrolMobName != null && patrolMap.HasValue)
        {
            ImGui.Separator();
            ImGui.Text("現在向かっている巡回地点");
            ImGui.Text($"Aモブ：{automation.CurrentPatrolMobName}  #{automation.CurrentPatrolPointNumber}/{automation.CurrentPatrolPointCount}");
            ImGui.Text($"MAP座標：X {patrolMap.Value.X:0.0} / Y {patrolMap.Value.Z:0.0}");
            if (patrolInternal.HasValue)
            {
                var p = patrolInternal.Value;
                ImGui.Text($"旗内部座標：X {p.X:0.00} / Z {p.Z:0.00}（Yはvnavmesh任せ）");
            }
            if (automation.Active && ImGui.Button("今の座標をスキップして次へ"))
                automation.SkipCurrentPatrolPoint();
        }
        ImGui.TextWrapped("巡回中は飛行を維持し、ObjectTableでAモブを常時監視します。Aモブを発見すると実座標を記録し、そのまま次の巡回地点へ進みます。");
        ImGui.Spacing();
        DrawStartSaveButtons();
        ImGui.Spacing(); ImGui.TextDisabled("v0.1.1");
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("設定"))
            {
                ImGui.BeginChild("##settings_scroll",new Vector2(0,0),false);
                DrawSettings();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("除外設定"))
            {
                ImGui.BeginChild("##exclusions_scroll",new Vector2(0,0),false);
                DrawExclusions();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("MOBハント"))
            {
                ImGui.BeginChild("##mobhunt_scroll",new Vector2(0,0),false);
                DrawMobHunt();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("探索記録"))
            {
                ImGui.BeginChild("##search_scroll",new Vector2(0,0),false);
                DrawRecords(automation.SearchRecords,true);
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("討伐記録"))
            {
                ImGui.BeginChild("##hunt_scroll",new Vector2(0,0),false);
                DrawRecords(automation.HuntRecords,false);
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("スキップ記録"))
            {
                ImGui.BeginChild("##skip_scroll",new Vector2(0,0),false);
                DrawSkipRecords();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawSettings()
    {
        ImGui.Text("巡回方式");
        if(ImGui.RadioButton("モブ単位巡回",config.PatrolMode=="Mob")&&!automation.Active)config.PatrolMode="Mob"; ImGui.SameLine();
        if(ImGui.RadioButton("最寄り座標巡回",config.PatrolMode=="Nearest")&&!automation.Active)config.PatrolMode="Nearest";
        var expansionPatrol=config.PatrolExpansionMaps;
        if(ImGui.Checkbox("選択した追加ディスク内の全MAPを巡回",ref expansionPatrol)&&!automation.Active)
        {
            config.PatrolExpansionMaps=expansionPatrol;
            if(expansionPatrol) config.PatrolAllInstances=false;
        }
        var worldPatrol=config.PatrolWorlds;
        if(ImGui.Checkbox("1ワールド終了後、選択DCの次ワールドへ",ref worldPatrol)&&!automation.Active)
        {
            config.PatrolWorlds=worldPatrol;
            if(worldPatrol){ config.PatrolExpansionMaps=true; config.PatrolAllInstances=false; }
        }
        ImGui.TextDisabled("ワールド巡回は選択したデータセンター内で行います。");

        ImGui.Separator();
        ImGui.Text("インスタンス巡回");
        var multiScope=config.PatrolExpansionMaps || config.PatrolWorlds;
        var allInstances=config.PatrolAllInstances;
        if(multiScope || automation.Active || instanceStage!=InstancePatrolStage.Idle) ImGui.BeginDisabled();
        if(ImGui.Checkbox("複数インスタンスがある場合は全て巡回する",ref allInstances)) config.PatrolAllInstances=allInstances;
        if(multiScope || automation.Active || instanceStage!=InstancePatrolStage.Idle) ImGui.EndDisabled();
        ImGui.TextDisabled("v0.1.1では『選択中の1MAP』で使用します。自動切替にはLifestreamが必要です。");
        ImGui.TextDisabled("MAP巡回完了後、エーテライトへ戻り、未巡回のインスタンスへ移動して同じMAPを再巡回します。");

        ImGui.Separator();
        ImGui.Text("引っかかり自動スキップ");
        var autoSkipStuck=config.AutoSkipStuckPatrolPoint;
        if(ImGui.Checkbox("停滞・往復・同じ場所のループを検知して自動スキップ",ref autoSkipStuck)&&!automation.Active) config.AutoSkipStuckPatrolPoint=autoSkipStuck;
        var skipSeconds=config.AutoSkipStuckSeconds;
        ImGui.SetNextItemWidth(100);
        if(ImGui.InputInt("判定秒数",ref skipSeconds)&&!automation.Active) config.AutoSkipStuckSeconds=Math.Clamp(skipSeconds,3,120);
        ImGui.TextDisabled("3～120秒。vnavmeshのメッシュ作業中・経路作成中はカウントしません。");
        ImGui.Separator();
        ImGui.Text("記録データの保存先");
        ImGui.TextDisabled("探索記録・討伐記録・スキップ記録・MOBハントで共通の保存先です。");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##record_output_dir_settings",ref recordOutputDirectory,512);
        if(ImGui.Button("保存先を適用"))
        {
            try
            {
                recordOutputDirectory=automation.SetRecordDirectory(recordOutputDirectory);
                config.RecordOutputDirectory=recordOutputDirectory;
                pi.SavePluginConfig(config);
            }
            catch { }
        }
        ImGui.SameLine();
        if(ImGui.Button("保存フォルダーを開く"))
        {
            try { Process.Start(new ProcessStartInfo{FileName=automation.RecordDirectory,UseShellExecute=true}); } catch { }
        }
        ImGui.SameLine();
        if(ImGui.Button("保存先リセット"))
        {
            try
            {
                recordOutputDirectory=automation.ResetRecordDirectory();
                config.RecordOutputDirectory="";
                pi.SavePluginConfig(config);
            }
            catch { }
        }
        ImGui.TextWrapped($"現在の保存先：{automation.RecordDirectory}");

        ImGui.Separator();
        DrawStartSaveButtons();
    }

    private void DrawStartSaveButtons()
    {
        var running=automation.Active || instanceStage!=InstancePatrolStage.Idle;
        var buttonColor=running?new Vector4(.9f,.3f,.25f,1):new Vector4(.2f,.65f,.3f,1);
        ImGui.PushStyleColor(ImGuiCol.Button,buttonColor);
        if(ImGui.Button(running?"停止":"開始",new Vector2(130,32))){if(running)Stop();else Start();}
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if(ImGui.Button("設定保存",new Vector2(130,32)))Save();
    }

    private void DrawSkipRecords()
    {
        var list=automation.SkipRecords;
        if(ImGui.Button("CSVを再出力")) automation.ExportSkipRecords();
        ImGui.SameLine();
        if(ImGui.Button("全件削除##skip_all")) ImGui.OpenPopup("スキップ記録を全件削除");
        if(ImGui.BeginPopup("スキップ記録を全件削除"))
        {
            ImGui.Text("スキップ記録をすべて削除します。よろしいですか？");
            if(ImGui.Button("削除する")){ automation.ClearSkipRecords(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if(ImGui.Button("キャンセル")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.Separator();
        ImGui.BeginChild("##skip_records",new Vector2(0,0),true);
        foreach(var r in list.Reverse().ToArray())
        {
            ImGui.TextUnformatted($"{r.Time.ToLocalTime():MM/dd HH:mm:ss}  {r.World}  {r.MapName}  {r.Mob}  #{r.PointNumber}");
            ImGui.TextDisabled($"MAP {r.MapX:0.0}, {r.MapY:0.0} / {r.Reason} / 判定 {r.ThresholdSeconds}秒 / {(r.Automatic?"自動":"手動")}");
            if(ImGui.Button($"削除##skip_{r.Time.Ticks}_{r.TerritoryId}_{r.Mob}_{r.PointNumber}")) automation.DeleteSkipRecord(r);
            ImGui.Separator();
        }
        ImGui.EndChild();
    }

    private void DrawExclusions()
    {
        ImGui.Text("除外MAP（チェック＝巡回しない）");
        ImGui.TextDisabled("探索巡回に適用します。");
        foreach(var expansion in HuntData.Expansions)
        {
            var zones=HuntData.GetZones(expansion.Value).Where(z=>z.HasPatrolData).ToArray();
            if(zones.Length==0) continue;
            if(ImGui.TreeNode($"{expansion.Label}##exclude_maps_{expansion.Value}"))
            {
                foreach(var zone in zones)
                {
                    var excluded=automation.IsMapExcluded(zone.TerritoryId);
                    if(ImGui.Checkbox($"{zone.JapaneseName}##exclude_map_{zone.TerritoryId}",ref excluded)&&!automation.Active)
                        automation.SetMapExcluded(zone.TerritoryId,excluded);
                }
                ImGui.TreePop();
            }
        }

        ImGui.Separator();
        ImGui.Text("除外MOB（チェック＝対象にしない）");
        ImGui.TextDisabled("チェックしたMOBは探索対象から除外します。");
        foreach(var expansion in HuntData.Expansions)
        {
            var mobs=HuntData.GetZones(expansion.Value).SelectMany(z=>z.Mobs).GroupBy(m=>m.CanonicalName,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToArray();
            if(mobs.Length==0) continue;
            if(ImGui.TreeNode($"{expansion.Label}##exclude_mobs_{expansion.Value}"))
            {
                foreach(var mob in mobs)
                {
                    var excluded=automation.IsExcluded(mob.CanonicalName);
                    if(ImGui.Checkbox($"{mob.DisplayName}##exclude_mob_{mob.CanonicalName}",ref excluded)&&!automation.Active)
                        automation.SetExcluded(mob.CanonicalName,excluded);
                }
                ImGui.TreePop();
            }
        }
    }

    private void DrawRecords(IReadOnlyList<HuntRecord> list,bool search)
    {
        if(ImGui.Button("CSVを再出力"))
        {
            if(search) automation.ExportSearchRecords(); else automation.ExportHuntRecords();
        }
        ImGui.SameLine();
        var popupId=search?"探索記録を全件削除":"討伐記録を全件削除";
        if(ImGui.Button($"全件削除##{(search?"search":"hunt")}_all")) ImGui.OpenPopup(popupId);
        if(ImGui.BeginPopup(popupId))
        {
            ImGui.Text($"{(search?"探索":"討伐")}記録をすべて削除します。よろしいですか？");
            if(ImGui.Button("削除する"))
            {
                if(search) automation.ClearSearchRecords(); else automation.ClearHuntRecords();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if(ImGui.Button("キャンセル")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.Separator();
        ImGui.BeginChild(search?"##search_records":"##hunt_records",new Vector2(0,0),true);
        foreach(var r in list.Reverse().ToArray())
        {
            var label=$"{r.Time.ToLocalTime():MM/dd HH:mm:ss}  {r.World}{InstanceLabel(r)}  {r.MapName}  {r.Mob}##record_{r.Time.Ticks}_{r.TerritoryId}_{r.Mob}_{r.Instance}";
            if(ImGui.Selectable(label,false)) PutRecordInChat(r);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("クリック：インスタンス＋MOB名＋フラッグ情報をチャット入力欄へ入れます（送信しません）");
            ImGui.TextDisabled($"MAP {r.MapX:0.0}, {r.MapY:0.0} / XYZ {r.X:0.0}, {r.Y:0.0}, {r.Z:0.0}");
            if(search)
            {
                if(ImGui.Button($"MOBハントへ追加##mobhunt_add_{r.Time.Ticks}_{r.TerritoryId}_{r.Mob}_{r.Instance}")) automation.AddMobHuntRecord(r);
                ImGui.SameLine();
            }
            if(ImGui.Button($"削除##{(search?"search":"hunt")}_{r.Time.Ticks}_{r.TerritoryId}_{r.Mob}_{r.Instance}"))
            {
                if(search) automation.DeleteSearchRecord(r); else automation.DeleteHuntRecord(r);
            }
            ImGui.Separator();
        }
        ImGui.EndChild();
    }


    private void DrawMobHunt()
    {
        var list=automation.MobHuntRecords;
        ImGui.Text("MOBハント用リスト");
        ImGui.TextDisabled("探索記録から必要な地点だけ追加して使う独立リストです。探索記録とは別に保存されます。");
        ImGui.TextDisabled("行クリック：インスタンス＋MOB名＋座標＋<flag>をチャット入力欄へ入れます（送信しません）。");
        ImGui.Separator();

        if(ImGui.Button("CSVを再出力")) automation.ExportMobHuntRecords();
        ImGui.SameLine();
        if(ImGui.Button("一括クリア")) ImGui.OpenPopup("MOBハントを一括クリア");
        if(ImGui.BeginPopup("MOBハントを一括クリア"))
        {
            ImGui.Text("MOBハント用リストだけをすべて削除します。よろしいですか？");
            ImGui.TextDisabled("探索・討伐・スキップ記録は削除されません。");
            if(ImGui.Button("削除する")){ automation.ClearMobHuntRecords(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if(ImGui.Button("キャンセル")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.Separator();

        for(var i=0;i<list.Count;i++)
        {
            var r=list[i];
            ImGui.PushID($"mobhunt_{i}_{r.Time.Ticks}_{r.TerritoryId}_{r.Mob}_{r.Instance}");
            ImGui.TextDisabled($"{i+1}."); ImGui.SameLine();
            var label=$"{r.World}{InstanceLabel(r)}  {r.MapName}  {r.Mob}  ({r.MapX:0.0}, {r.MapY:0.0})";
            if(ImGui.Selectable(label,false)) PutRecordInChat(r);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("クリック：インスタンス＋MOB名＋座標＋<flag>をチャット入力欄へ入れます（送信しません）");

            if(i>0)
            {
                if(ImGui.SmallButton("↑")){ automation.MoveMobHuntRecord(i,-1); ImGui.PopID(); break; }
            }
            else ImGui.BeginDisabled();
            if(i==0){ ImGui.SmallButton("↑"); ImGui.EndDisabled(); }
            ImGui.SameLine();
            if(i<list.Count-1)
            {
                if(ImGui.SmallButton("↓")){ automation.MoveMobHuntRecord(i,1); ImGui.PopID(); break; }
            }
            else
            {
                ImGui.BeginDisabled(); ImGui.SmallButton("↓"); ImGui.EndDisabled();
            }
            ImGui.SameLine();
            if(ImGui.SmallButton("削除")){ automation.DeleteMobHuntRecord(r); ImGui.PopID(); break; }
            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawDataCenter()
    {
        ImGui.SetNextItemWidth(230);
        if(!ImGui.BeginCombo("データセンター",config.SelectedDataCenter)) return;
        foreach(var dc in HuntData.DataCenters)
        {
            var selected=config.SelectedDataCenter==dc;
            if(ImGui.Selectable(dc,selected)&&!automation.Active)
            {
                config.SelectedDataCenter=dc;
                if(config.SelectedWorld!="現在のワールド" && !HuntData.GetWorlds(dc).Contains(config.SelectedWorld)) config.SelectedWorld="現在のワールド";
            }
            if(selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private void DrawWorld()
    {
        ImGui.SetNextItemWidth(230);
        if(!ImGui.BeginCombo("ワールド",config.SelectedWorld))return;
        foreach(var w in HuntData.GetWorldChoices(config.SelectedDataCenter)){var selected=config.SelectedWorld==w;if(ImGui.Selectable(w,selected)&&!automation.Active)config.SelectedWorld=w;if(selected)ImGui.SetItemDefaultFocus();}
        ImGui.EndCombo();
    }
    private void DrawExpansion()
    {
        var label=HuntData.Expansions.FirstOrDefault(x=>x.Value==config.SelectedExpansion).Label??config.SelectedExpansion;
        ImGui.SetNextItemWidth(230); if(!ImGui.BeginCombo("追加ディスク",label))return;
        foreach(var e in HuntData.Expansions){var s=config.SelectedExpansion==e.Value;if(ImGui.Selectable(e.Label,s)&&!automation.Active){config.SelectedExpansion=e.Value;var f=HuntData.GetZones(e.Value).FirstOrDefault();if(f!=null)config.SelectedTerritoryId=f.TerritoryId;}if(s)ImGui.SetItemDefaultFocus();}
        ImGui.EndCombo();
    }
    private void DrawMap()
    {
        var zones=HuntData.GetZones(config.SelectedExpansion);var z=zones.FirstOrDefault(x=>x.TerritoryId==config.SelectedTerritoryId);
        ImGui.SetNextItemWidth(230);if(!ImGui.BeginCombo("MAP",z?.JapaneseName??"選択してください"))return;
        foreach(var x in zones){var s=x.TerritoryId==config.SelectedTerritoryId;var text=x.JapaneseName+(x.HasPatrolData?" [巡回可]":" [情報のみ]");if(ImGui.Selectable(text,s)&&!automation.Active)config.SelectedTerritoryId=x.TerritoryId;if(s)ImGui.SetItemDefaultFocus();}
        ImGui.EndCombo();
    }

    public void Dispose()
    {
        automation.Stop(); lan.Dispose(); framework.Update-=OnUpdate; pi.UiBuilder.Draw-=Draw; pi.UiBuilder.OpenConfigUi-=Open; pi.UiBuilder.OpenMainUi-=Open; commands.RemoveHandler("/arankhunttour");
    }
}
