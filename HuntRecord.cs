using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ARankHuntTourAssistant;

public sealed record HuntRecord(
    DateTime Time,
    string World,
    string Expansion,
    uint TerritoryId,
    string MapName,
    string Mob,
    float MapX,
    float MapY,
    float X,
    float Y,
    float Z,
    string Kind,
    int Instance = 0);

public sealed record PatrolSkipRecord(
    DateTime Time,
    string World,
    string Expansion,
    uint TerritoryId,
    string MapName,
    string Mob,
    int PointNumber,
    float MapX,
    float MapY,
    float X,
    float Y,
    float Z,
    string Reason,
    int ThresholdSeconds,
    bool Automatic);

internal sealed class HuntRecordStore
{
    private readonly string defaultDir;
    private string dir;
    private readonly object gate = new();
    private readonly Func<int>? instanceProvider;
    public List<HuntRecord> SearchRecords { get; } = new();
    public List<HuntRecord> HuntRecords { get; } = new();
    public List<PatrolSkipRecord> SkipRecords { get; } = new();
    public List<HuntRecord> MobHuntRecords { get; } = new();
    public string DirectoryPath => dir;

    public HuntRecordStore(string configDirectory, string? configuredDirectory=null, Func<int>? instanceProvider=null)
    {
        defaultDir = Path.Combine(configDirectory, "records");
        dir = ResolveDirectory(configuredDirectory);
        this.instanceProvider = instanceProvider;
        Directory.CreateDirectory(dir);
        LoadJson(SearchJsonPath, SearchRecords);
        LoadJson(HuntJsonPath, HuntRecords);
        LoadSkipJson(SkipJsonPath, SkipRecords);
        LoadJson(MobHuntJsonPath, MobHuntRecords);
    }

    public string SetDirectory(string? path)
    {
        lock(gate)
        {
            var next=ResolveDirectory(path);
            Directory.CreateDirectory(next);
            dir=next;
            WriteAll(SearchPath,SearchRecords); WriteJson(SearchJsonPath,SearchRecords);
            WriteAll(HuntPath,HuntRecords); WriteJson(HuntJsonPath,HuntRecords);
            WriteAllSkips(SkipPath,SkipRecords); WriteSkipJson(SkipJsonPath,SkipRecords);
            WriteAll(MobHuntPath,MobHuntRecords); WriteJson(MobHuntJsonPath,MobHuntRecords);
            return dir;
        }
    }

    public string ResetDirectory()=>SetDirectory(null);

    private string ResolveDirectory(string? path)
    {
        if(string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(defaultDir);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

    private string SearchPath => Path.Combine(dir, "探索記録.csv");
    private string HuntPath => Path.Combine(dir, "討伐記録.csv");
    private string SearchJsonPath => Path.Combine(dir, "探索記録.json");
    private string HuntJsonPath => Path.Combine(dir, "討伐記録.json");
    private string SkipPath => Path.Combine(dir, "スキップ記録.csv");
    private string SkipJsonPath => Path.Combine(dir, "スキップ記録.json");
    private string MobHuntPath => Path.Combine(dir, "MOBハント.csv");
    private string MobHuntJsonPath => Path.Combine(dir, "MOBハント.json");

    private HuntRecord AttachCurrentInstance(HuntRecord record)
    {
        if(record.Instance>0 || instanceProvider==null) return record;
        try
        {
            var instance=instanceProvider();
            return instance>0 ? record with { Instance=instance } : record;
        }
        catch { return record; }
    }

    public void AddSearch(HuntRecord record) { lock(gate){ record=AttachCurrentInstance(record); if(IsDuplicate(SearchRecords,record)) return; SearchRecords.Add(record); WriteAll(SearchPath,SearchRecords); WriteJson(SearchJsonPath,SearchRecords); } }
    public void AddHunt(HuntRecord record) { lock(gate){ record=AttachCurrentInstance(record); if(IsDuplicate(HuntRecords,record)) return; HuntRecords.Add(record); WriteAll(HuntPath,HuntRecords); WriteJson(HuntJsonPath,HuntRecords); } }
    public void AddSkip(PatrolSkipRecord record) { lock(gate){ SkipRecords.Add(record); WriteAllSkips(SkipPath,SkipRecords); WriteSkipJson(SkipJsonPath,SkipRecords); } }
    public void ExportSearch(){ lock(gate){ WriteAll(SearchPath,SearchRecords); WriteJson(SearchJsonPath,SearchRecords); } }
    public void ExportHunt(){ lock(gate){ WriteAll(HuntPath,HuntRecords); WriteJson(HuntJsonPath,HuntRecords); } }
    public void ExportSkips(){ lock(gate){ WriteAllSkips(SkipPath,SkipRecords); WriteSkipJson(SkipJsonPath,SkipRecords); } }
    public void ExportMobHunt(){ lock(gate){ WriteAll(MobHuntPath,MobHuntRecords); WriteJson(MobHuntJsonPath,MobHuntRecords); } }
    public void DeleteSearch(HuntRecord record){ lock(gate){ SearchRecords.Remove(record); WriteAll(SearchPath,SearchRecords); WriteJson(SearchJsonPath,SearchRecords); } }
    public void DeleteHunt(HuntRecord record){ lock(gate){ HuntRecords.Remove(record); WriteAll(HuntPath,HuntRecords); WriteJson(HuntJsonPath,HuntRecords); } }
    public void DeleteSkip(PatrolSkipRecord record){ lock(gate){ SkipRecords.Remove(record); WriteAllSkips(SkipPath,SkipRecords); WriteSkipJson(SkipJsonPath,SkipRecords); } }
    public void ClearSearch(){ lock(gate){ SearchRecords.Clear(); WriteAll(SearchPath,SearchRecords); WriteJson(SearchJsonPath,SearchRecords); } }
    public void ClearHunt(){ lock(gate){ HuntRecords.Clear(); WriteAll(HuntPath,HuntRecords); WriteJson(HuntJsonPath,HuntRecords); } }
    public void ClearSkips(){ lock(gate){ SkipRecords.Clear(); WriteAllSkips(SkipPath,SkipRecords); WriteSkipJson(SkipJsonPath,SkipRecords); } }
    public void AddMobHunt(HuntRecord record)
    {
        lock(gate)
        {
            // MOBハント用リストは探索記録が持っているインスタンス番号をそのまま引き継ぐ。
            // v0.1.0以前のInstance=0記録へ「現在地」の番号を後付けしない。
            if(MobHuntRecords.Contains(record)) return;
            MobHuntRecords.Add(record);
            WriteAll(MobHuntPath,MobHuntRecords); WriteJson(MobHuntJsonPath,MobHuntRecords);
        }
    }
    public void DeleteMobHunt(HuntRecord record)
    {
        lock(gate)
        {
            MobHuntRecords.Remove(record);
            WriteAll(MobHuntPath,MobHuntRecords); WriteJson(MobHuntJsonPath,MobHuntRecords);
        }
    }
    public void ClearMobHunt()
    {
        lock(gate)
        {
            MobHuntRecords.Clear();
            WriteAll(MobHuntPath,MobHuntRecords); WriteJson(MobHuntJsonPath,MobHuntRecords);
        }
    }
    public void MoveMobHunt(int index,int delta)
    {
        lock(gate)
        {
            var next=index+delta;
            if(index<0 || index>=MobHuntRecords.Count || next<0 || next>=MobHuntRecords.Count) return;
            (MobHuntRecords[index],MobHuntRecords[next])=(MobHuntRecords[next],MobHuntRecords[index]);
            WriteAll(MobHuntPath,MobHuntRecords); WriteJson(MobHuntJsonPath,MobHuntRecords);
        }
    }

    private static bool IsDuplicate(List<HuntRecord> list,HuntRecord r)
        => list.Any(x=>x.World.Equals(r.World,StringComparison.OrdinalIgnoreCase) && x.Instance==r.Instance && x.TerritoryId==r.TerritoryId && x.Mob.Equals(r.Mob,StringComparison.OrdinalIgnoreCase) && MathF.Abs(x.X-r.X)<2f && MathF.Abs(x.Z-r.Z)<2f && (r.Time-x.Time).Duration()<TimeSpan.FromMinutes(5));

    private static void WriteAll(string path,List<HuntRecord> records)
    {
        using var sw=new StreamWriter(path,false,new UTF8Encoding(true));
        sw.WriteLine("時刻,種別,ワールド,インスタンス,追加ディスク,Territory,MAP,Aモブ,MAP_X,MAP_Y,内部_X,内部_Y,内部_Z");
        foreach(var r in records)
            sw.WriteLine(string.Join(',', Csv(r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),Csv(r.Kind),Csv(r.World),r.Instance,Csv(r.Expansion),r.TerritoryId,Csv(r.MapName),Csv(r.Mob),F(r.MapX),F(r.MapY),F(r.X),F(r.Y),F(r.Z)));
    }
    private static string F(float v)=>v.ToString("0.000",CultureInfo.InvariantCulture);
    private static string Csv(string s)=>'"'+s.Replace("\"","\"\"")+'"';
    private static void WriteJson(string path,List<HuntRecord> records)
        => File.WriteAllText(path,JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));

    private static void WriteAllSkips(string path,List<PatrolSkipRecord> records)
    {
        using var sw=new StreamWriter(path,false,new UTF8Encoding(true));
        sw.WriteLine("時刻,ワールド,追加ディスク,Territory,MAP,Aモブ,巡回ポイント,MAP_X,MAP_Y,内部_X,内部_Y,内部_Z,理由,判定秒数,自動");
        foreach(var r in records)
            sw.WriteLine(string.Join(',', Csv(r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),Csv(r.World),Csv(r.Expansion),r.TerritoryId,Csv(r.MapName),Csv(r.Mob),r.PointNumber,F(r.MapX),F(r.MapY),F(r.X),F(r.Y),F(r.Z),Csv(r.Reason),r.ThresholdSeconds,r.Automatic?"1":"0"));
    }
    private static void WriteSkipJson(string path,List<PatrolSkipRecord> records)
        => File.WriteAllText(path,JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
    private static void LoadSkipJson(string path,List<PatrolSkipRecord> target)
    {
        try
        {
            if(!File.Exists(path)) return;
            var loaded=JsonSerializer.Deserialize<List<PatrolSkipRecord>>(File.ReadAllText(path));
            if(loaded!=null) target.AddRange(loaded);
        }
        catch { }
    }

    private static void LoadJson(string path,List<HuntRecord> target)
    {
        try
        {
            if(!File.Exists(path)) return;
            var loaded=JsonSerializer.Deserialize<List<HuntRecord>>(File.ReadAllText(path));
            if(loaded!=null) target.AddRange(loaded);
        }
        catch { }
    }
}
