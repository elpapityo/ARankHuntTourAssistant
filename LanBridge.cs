using System.Numerics;
using Dalamud.Plugin.Services;

namespace ARankHuntTourAssistant;

// LAN通信機能は使用しません。
internal sealed class LanBridge : IDisposable
{
    public event Action<MobNotice>? PacketReceived;
    public string Status => "通信なし";
    public LanBridge(IPluginLog log) { }
    public void Start(Configuration cfg) { }
    public Task SendMobAsync(string mob, uint territory, Vector3 p) => Task.CompletedTask;
    public void Dispose() { }
}
