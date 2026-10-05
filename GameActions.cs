using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ARankHuntTourAssistant;

internal sealed class GameActions
{
    private readonly ICommandManager commands;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> vnavReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> vnavMoveTo;
    private readonly ICallGateSubscriber<bool> vnavPathfind;
    private readonly ICallGateSubscriber<bool> vnavRunning;
    private readonly ICallGateSubscriber<object> vnavStop;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> vnavPointOnFloor;

    private readonly ICallGateSubscriber<bool> lifestreamBusy;
    private readonly ICallGateSubscriber<bool> lifestreamCanChangeInstance;
    private readonly ICallGateSubscriber<int> lifestreamNumberOfInstances;
    private readonly ICallGateSubscriber<int, object> lifestreamChangeInstance;

    public bool OwnsVnav { get; private set; }
    public bool OwnsCombat { get; private set; }
    public Vector3? LastDestination { get; private set; }

    public GameActions(IDalamudPluginInterface pi, ICommandManager commands, ICondition condition, IPluginLog log)
    {
        this.commands = commands; this.condition = condition; this.log = log;
        vnavReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        vnavMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        vnavPathfind = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        vnavRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        vnavStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        vnavPointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");

        // Lifestream は EzIPC で "Lifestream.<MethodName>" を公開している。
        lifestreamBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lifestreamCanChangeInstance = pi.GetIpcSubscriber<bool>("Lifestream.CanChangeInstance");
        lifestreamNumberOfInstances = pi.GetIpcSubscriber<int>("Lifestream.GetNumberOfInstances");
        lifestreamChangeInstance = pi.GetIpcSubscriber<int, object>("Lifestream.ChangeInstance");
    }

    public bool IsReady() { try { return vnavReady.InvokeFunc(); } catch { return false; } }
    public bool IsPathfinding() { try { return vnavPathfind.InvokeFunc(); } catch { return false; } }
    public bool IsPathRunning() { try { return vnavRunning.InvokeFunc(); } catch { return false; } }
    public bool IsBusy() { try { return IsPathfinding() || IsPathRunning(); } catch { return false; } }
    public bool IsMounted => condition[ConditionFlag.Mounted];
    public bool IsFlying => condition[ConditionFlag.InFlight];
    public bool IsDiving => condition[ConditionFlag.Diving];
    public bool IsInCombat => condition[ConditionFlag.InCombat];
    public bool IsBetweenAreas => condition[ConditionFlag.BetweenAreas];

    public bool IsLifestreamAvailable =>
        lifestreamBusy.HasFunction &&
        lifestreamCanChangeInstance.HasFunction &&
        lifestreamNumberOfInstances.HasFunction &&
        lifestreamChangeInstance.HasAction;
    public bool IsLifestreamBusy()
    {
        try { return lifestreamBusy.HasFunction && lifestreamBusy.InvokeFunc(); }
        catch { return true; }
    }
    public bool CanChangeInstance()
    {
        try { return lifestreamCanChangeInstance.HasFunction && lifestreamCanChangeInstance.InvokeFunc(); }
        catch { return false; }
    }
    public int NumberOfInstances()
    {
        try { return lifestreamNumberOfInstances.HasFunction ? lifestreamNumberOfInstances.InvokeFunc() : 0; }
        catch { return 0; }
    }
    public bool ChangeInstance(int number)
    {
        if(number <= 0 || !lifestreamChangeInstance.HasAction) return false;
        try
        {
            lifestreamChangeInstance.InvokeAction(number);
            log.Information("[ARHTA][Instance] Lifestreamへインスタンス移動要求 / Target={Instance}", number);
            return true;
        }
        catch(Exception ex)
        {
            log.Warning(ex,"[ARHTA][Instance] Lifestreamインスタンス移動要求に失敗 / Target={Instance}", number);
            return false;
        }
    }

    public Vector3? PointOnFloor(Vector3 point)
    {
        try { return vnavPointOnFloor.InvokeFunc(point, false, 100f); } catch { return null; }
    }

    public bool Move(Vector3 destination, bool fly)
    {
        if (!IsReady()) return false;
        try
        {
            var ok = vnavMoveTo.InvokeFunc(destination, fly);
            if (ok) { OwnsVnav = true; LastDestination = destination; }
            return ok;
        }
        catch (Exception ex) { log.Debug(ex, "[AMH] vnav move failed"); return false; }
    }


    public bool FlyToFlag()
    {
        if (!IsReady()) return false;
        try
        {
            commands.ProcessCommand("/vnav flyflag");
            OwnsVnav = true;
            LastDestination = null;
            return true;
        }
        catch (Exception ex) { log.Debug(ex, "[AMH] vnav flyflag failed"); return false; }
    }

    public void StopOwnedVnav()
    {
        if (!OwnsVnav) return;
        try { vnavStop.InvokeAction(); } catch { }
        try { commands.ProcessCommand("/vnav stop"); } catch { }
        OwnsVnav = false; LastDestination = null;
    }

    public void ForceStopVnav()
    {
        try { vnavStop.InvokeAction(); } catch { }
        try { commands.ProcessCommand("/vnav stop"); } catch { }
        OwnsVnav = false; LastDestination = null;
    }

    public void ForceClearOwnedVnavFlag() { OwnsVnav = false; LastDestination = null; }

    public void ForceStopCombatAutomation()
    {
        try { commands.ProcessCommand("/rotation Off"); } catch { }
        try { commands.ProcessCommand("/wrath auto off"); } catch { }
        try { commands.ProcessCommand("/bmrai off"); } catch { }
        OwnsCombat = false;
        log.Information("[AMH] 強制戦闘停止 / RSR Off / Wrath Off / BMR Off");
    }

    public void SetCombat(bool enabled)
    {
        if (enabled)
        {
            if (OwnsCombat) return;
            commands.ProcessCommand("/rotation Settings TargetingTypes add HighMaxHP");
            commands.ProcessCommand("/rotation Auto HighMaxHP");
            log.Information("[AMH] RSR直接設定 / TargetingTypes add HighMaxHP -> /rotation Auto HighMaxHP");
            OwnsCombat = true;
            return;
        }
        if (!OwnsCombat) return;
        commands.ProcessCommand("/rotation Off");
        OwnsCombat = false;
    }

    public unsafe bool UseGysahlGreens()
    {
        try
        {
            var agent = AgentInventoryContext.Instance();
            if (agent == null)
            {
                log.Warning("[AMH] ギサールの野菜使用失敗 / AgentInventoryContext=null");
                return false;
            }

            var result = agent->UseItem(4868, InventoryType.Invalid, 0, 0);
            log.Information("[AMH] ギサールの野菜使用要求 / itemId=4868 result={Result}", result);
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[AMH] Gysahl Greens use failed");
            return false;
        }
    }

    public unsafe float ChocoboTimeLeft()
    {
        try
        {
            var ui = UIState.Instance();
            return ui == null ? 0f : ui->Buddy.CompanionInfo.TimeLeft;
        }
        catch { return 0f; }
    }

    public unsafe uint[] HateEntityIds()
    {
        try
        {
            var ui = UIState.Instance();
            if (ui == null) return [];
            var hate = &ui->Hate;
            var count = Math.Clamp(hate->HateArrayLength, 0, 32);
            if (count == 0) return [];
            var entries = (HateInfo*)hate;
            var result = new List<uint>(count);
            for (var i = 0; i < count; i++)
                if (entries[i].EntityId != 0) result.Add(entries[i].EntityId);
            return result.ToArray();
        }
        catch { return []; }
    }

    public unsafe bool SetChatInput(ReadOnlySpan<byte> encoded)
    {
        try
        {
            var mod=RaptureAtkModule.Instance();
            if(mod==null) return false;
            var basePtr=mod->TextInput.TargetTextInputEventInterface;
            if(basePtr==null) return false;
            var component=(AtkComponentTextInput*)((AtkComponentInputBase*)basePtr - 1);
            var addon=component->OwnerAddon;
            if(addon==null) addon=component->ContainingAddon2;
            if(addon==null || addon->NameString!="ChatLog") return false;
            var buffer=new byte[encoded.Length+1];
            encoded.CopyTo(buffer);
            buffer[^1]=0;
            component->SetText(buffer);
            return true;
        }
        catch(Exception ex)
        {
            log.Debug(ex,"[AMH] chat input set failed");
            return false;
        }
    }


    public void Teleport(string aetheryte) => commands.ProcessCommand($"/li tp {aetheryte}");
    public void Aethernet(string destination) => commands.ProcessCommand($"/li {destination}");

    public unsafe bool UseGeneralAction(uint actionId)
    {
        try
        {
            var am = ActionManager.Instance();
            return am != null && am->UseAction(ActionType.GeneralAction, actionId);
        }
        catch { return false; }
    }

    public bool ToggleMount() => UseGeneralAction(9);
    public bool TakeOff() => UseGeneralAction(2);
}
