using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using visland.Helpers;

namespace visland.Island;

// 無人島採集點掃描（偵錯用）：預設關、只由使用者手動開始／停止，載入／進島／登入都不會自己開始。
// 契約：只讀遊戲已解析的資料（物件表、目標、收納袋數量），每次讀到就抄成值；不存 IGameObject、不跨幀持有指標。
// 產出：插件設定資料夾裡每次開始一個 island_node_scan_<時間>.json，給離線路線產生器（tools/island）吃。
public sealed class IslandNodeScan : IDisposable {
    public const uint GatherObjectBaseId = 2012985;
    private const uint IslandTerritory = 1055;
    private const long ScanIntervalMs = 1000;
    private const long SaveIntervalMs = 30_000;
    private const long AttributeWindowMs = 10_000;
    private const float NearestFallbackMeters = 5f;
    private const int MaxTransitionsPerNode = 100;
    private const long ErrorLogCooldownMs = 60_000;

    private sealed class NodeRecord {
        public string Name = "";
        public float X, Y, Z;
        public bool Targetable;
        public bool SeenTargetable, SeenUntargetable;
        public DateTime FirstSeen, LastSeen;
        public List<string> OtherNames = [];
        public List<(DateTime Time, bool Targetable)> Transitions = [];
    }

    private sealed class YieldRecord {
        public DateTime Time;
        public string Name = "";
        public float X, Y, Z;
        public string Via = "";
        public List<(uint ItemId, int Delta)> Items = [];
    }

    private readonly Dictionary<(int, int, int), NodeRecord> _nodes = [];
    private readonly List<YieldRecord> _yields = [];
    private Dictionary<uint, int>? _lastCounts;
    private uint[] _itemIds = [];
    private (string Name, Vector3 Pos, long Tick)? _lastTarget;
    private long _nextScan, _nextSave, _nextErrorLog;
    private bool _dirty;
    private DateTime _started;
    private string? _path;

    public bool Running { get; private set; }
    public int NodeCount => _nodes.Count;
    public int YieldCount => _yields.Count;
    public int Unattributed { get; private set; }
    public string? FileName => _path == null ? null : Path.GetFileName(_path);
    public string? LastSaveError { get; private set; }

    public void Start() {
        if (Running)
            return;
        _nodes.Clear();
        _yields.Clear();
        _lastCounts = null;
        _lastTarget = null;
        Unattributed = 0;
        LastSaveError = null;
        _dirty = false;
        _started = DateTime.UtcNow;
        _path = Path.Combine(Service.Interface.ConfigDirectory.FullName, $"island_node_scan_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        _nextScan = _nextSave = Environment.TickCount64;
        Running = true;
        Service.Framework.Update += OnUpdate;
        Service.Log.Information($"[無人島採集點掃描] 開始，結果存到插件設定資料夾的 {FileName}");
    }

    public void Stop() {
        if (!Running)
            return;
        Service.Framework.Update -= OnUpdate;
        Running = false;
        Save();
        Service.Log.Information($"[無人島採集點掃描] 停止：採集點 {_nodes.Count} 個、收穫 {_yields.Count} 筆、對不到採集點的收穫 {Unattributed} 次，檔案 {FileName}");
    }

    public void Dispose() {
        try {
            Stop();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[無人島採集點掃描] 卸載時存檔失敗");
        }
    }

    // 本處理器自己吃掉例外：同一插件的 Framework.Update 共用一個 try/catch，擲出去會讓後面的處理器（路線執行）那一幀被跳過。
    private void OnUpdate(IFramework _) {
        try {
            Tick();
        }
        catch (Exception ex) {
            var now = Environment.TickCount64;
            if (now >= _nextErrorLog) {
                _nextErrorLog = now + ErrorLogCooldownMs;
                Service.Log.Warning(ex, "[無人島採集點掃描] 這一輪讀取失敗，下一秒再試");
            }
        }
    }

    private void Tick() {
        if (!Service.ClientState.IsLoggedIn || Player.Territory != IslandTerritory)
            return;
        var now = Environment.TickCount64;
        TrackTarget(now);
        if (now < _nextScan)
            return;
        _nextScan = now + ScanIntervalMs;
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
            return;
        ScanObjects();
        ReadYields(now);
        if (_dirty && now >= _nextSave) {
            _nextSave = now + SaveIntervalMs;
            Save();
        }
    }

    // 每幀只看目前目標是不是島上採集物件（採集動畫期間目標可能被清掉，所以記住最後一次）。
    private void TrackTarget(long now) {
        var t = Service.TargetManager.Target;
        if (t == null || t.BaseId != GatherObjectBaseId)
            return;
        var pos = t.Position;
        _lastTarget = _lastTarget is { } lt && lt.Pos == pos ? (lt.Name, pos, now) : (t.Name.TextValue, pos, now);
    }

    private static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 10), (int)MathF.Round(p.Y * 10), (int)MathF.Round(p.Z * 10));

    private void ScanObjects() {
        var stamp = DateTime.UtcNow;
        foreach (var o in Service.ObjectTable) {
            if (o.BaseId != GatherObjectBaseId)
                continue;
            var pos = o.Position;
            var targetable = o.IsTargetable;
            var name = o.Name.TextValue;
            var key = Key(pos);
            if (!_nodes.TryGetValue(key, out var rec)) {
                rec = new NodeRecord { Name = name, X = pos.X, Y = pos.Y, Z = pos.Z, Targetable = targetable, FirstSeen = stamp };
                _nodes[key] = rec;
                _dirty = true;
            }
            else {
                if (rec.Targetable != targetable) {
                    if (rec.Transitions.Count < MaxTransitionsPerNode)
                        rec.Transitions.Add((stamp, targetable));
                    rec.Targetable = targetable;
                    _dirty = true;
                }
                // 名字變了＝同一個佈局點換了種類；照實記下來，離線工具據此把該點標成不固定
                if (name != rec.Name && !rec.OtherNames.Contains(name)) {
                    rec.OtherNames.Add(name);
                    _dirty = true;
                }
            }
            rec.LastSeen = stamp;
            rec.SeenTargetable |= targetable;
            rec.SeenUntargetable |= !targetable;
        }
    }

    // 收納袋讀法與缺料總表相同（Utils.NumItems；MJIManager 拿不到就不讀）；讀到整份 0 視為資料未載入。
    private unsafe void ReadYields(long now) {
        if (MJIManager.Instance() == null)
            return;
        if (_itemIds.Length == 0)
            _itemIds = [.. MaterialSources.All.Where(m => m.Gather != null && m.ItemId != 0).Select(m => m.ItemId)];
        if (_itemIds.Length == 0)
            return;
        var counts = new Dictionary<uint, int>(_itemIds.Length);
        var total = 0;
        foreach (var id in _itemIds) {
            var c = Utils.NumItems(id);
            counts[id] = c;
            total += c;
        }
        if (total == 0 && _lastCounts != null && _lastCounts.Values.Any(v => v > 0))
            return;
        if (_lastCounts != null) {
            List<(uint, int)>? items = null;
            foreach (var (id, c) in counts)
                if (_lastCounts.TryGetValue(id, out var prev) && c > prev)
                    (items ??= []).Add((id, c - prev));
            if (items != null)
                Attribute(items, now);
        }
        _lastCounts = counts;
    }

    private void Attribute(List<(uint ItemId, int Delta)> items, long now) {
        string name, via;
        Vector3 pos;
        if (_lastTarget is { } lt && now - lt.Tick <= AttributeWindowMs) {
            (name, pos, via) = (lt.Name, lt.Pos, "target");
        }
        else if (Nearest(Player.Position) is { } n) {
            (name, pos, via) = (n.Name, new Vector3(n.X, n.Y, n.Z), "nearest");
        }
        else {
            ++Unattributed;
            Service.Log.Information($"[無人島採集點掃描] 收納袋增加但附近沒有採集點（可能是倉庫／耕地／牧場）：{Describe(items)}");
            return;
        }
        _yields.Add(new YieldRecord { Time = DateTime.UtcNow, Name = name, X = pos.X, Y = pos.Y, Z = pos.Z, Via = via, Items = items });
        _dirty = true;
        Service.Log.Information($"[無人島採集點掃描] 收穫 {name} ({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}) via={via}：{Describe(items)}");
    }

    private static string Describe(List<(uint ItemId, int Delta)> items) => string.Join(", ", items.Select(i => $"{i.ItemId}+{i.Delta}"));

    private NodeRecord? Nearest(Vector3 p) {
        NodeRecord? best = null;
        var bestD = NearestFallbackMeters * NearestFallbackMeters;
        foreach (var n in _nodes.Values) {
            var d = (new Vector3(n.X, n.Y, n.Z) - p).LengthSquared();
            if (d <= bestD) {
                bestD = d;
                best = n;
            }
        }
        return best;
    }

    private void Save() {
        if (_path == null || (!_dirty && File.Exists(_path)))
            return;
        try {
            var doc = new {
                version = 1,
                territory = IslandTerritory,
                started = _started,
                saved = DateTime.UtcNow,
                unattributed = Unattributed,
                nodes = _nodes.Values.Select(n => new {
                    baseId = GatherObjectBaseId, name = n.Name, x = n.X, y = n.Y, z = n.Z,
                    targetable = n.Targetable, seenTargetable = n.SeenTargetable, seenUntargetable = n.SeenUntargetable,
                    firstSeen = n.FirstSeen, lastSeen = n.LastSeen, otherNames = n.OtherNames,
                    transitions = n.Transitions.Select(t => new { time = t.Time, targetable = t.Targetable }),
                }),
                yields = _yields.Select(y => new {
                    time = y.Time, name = y.Name, x = y.X, y = y.Y, z = y.Z, via = y.Via,
                    items = y.Items.Select(i => new { itemId = i.ItemId, delta = i.Delta }),
                }),
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(doc, Formatting.Indented), new UTF8Encoding(false));
            File.Move(tmp, _path, true);
            _dirty = false;
            LastSaveError = null;
        }
        catch (Exception ex) {
            LastSaveError = ex.Message;
            var now = Environment.TickCount64;
            if (now >= _nextErrorLog) {
                _nextErrorLog = now + ErrorLogCooldownMs;
                Service.Log.Warning(ex, $"[無人島採集點掃描] 存檔失敗：{FileName}");
            }
        }
    }
}
