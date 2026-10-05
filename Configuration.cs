using Dalamud.Configuration;

namespace ARankHuntTourAssistant;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string Role { get; set; } = "Solo";
    public string HuntMode { get; set; } = "Search";
    public string PatrolMode { get; set; } = "Nearest";
    // 互換用。UIには表示しません。
    public bool SummonChocoboBeforeCombat { get; set; } = false;
    public bool AlwaysRecordHunts { get; set; } = true;
    public string ParentAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 42557;
    public List<string> ExcludedMobs { get; set; } = new();
    public List<uint> ExcludedTerritories { get; set; } = new() { 398 };
    public string SelectedDataCenter { get; set; } = "Meteor";
    public string SelectedWorld { get; set; } = "現在のワールド";
    public string SelectedExpansion { get; set; } = "ShB";
    public uint SelectedTerritoryId { get; set; } = 813;
    public bool PatrolExpansionMaps { get; set; } = false;
    public bool PatrolWorlds { get; set; } = false;
    public bool PatrolAllInstances { get; set; } = false;
    public bool AutoSkipStuckPatrolPoint { get; set; } = true;
    public int AutoSkipStuckSeconds { get; set; } = 10;
    public string RecordOutputDirectory { get; set; } = "";
    public bool PlaySoundOnFound { get; set; } = true;
}
