using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace visland.Helpers;

public class Configuration : IDisposable {
    // base class for configuration nodes
    public abstract class Node {
        public event EventHandler? Modified;

        // notify that configuration node was modified; should be called by derived classes whenever they make any modifications
        // implementation dispatches modification event
        // root subscribes to modification event to save updated configuration
        public void NotifyModified() {
            Modified?.Invoke(this, EventArgs.Empty);
        }

        // deserialize fields from json; default implementation should work fine for most cases
        public virtual void Deserialize(JObject j, JsonSerializer ser) {
            var type = GetType();
            foreach ((var f, var data) in j) {
                var field = type.GetField(f);
                if (field != null) {
                    var value = data?.ToObject(field.FieldType, ser);
                    if (value != null) {
                        field.SetValue(this, value);
                    }
                }
            }
        }

        // serialize node to json; default implementation should work fine for most cases
        public virtual JObject Serialize(JsonSerializer ser) {
            return JObject.FromObject(this, ser);
        }

        // 讀檔沒擲例外、但有資料被丟掉時回一句說明（呼叫一次就清掉）；用方法不用屬性，免得被預設 Serialize 寫進檔。
        internal virtual string? TakeLoadProblem() => null;
    }

    private const int _version = 3;

    public event EventHandler? Modified;
    private readonly Dictionary<Type, Node> _nodes = [];
    private readonly EventHandler _forwardNodeModified;
    private EventHandler? _saveHandler;
    private FileInfo? _file;

    public IEnumerable<Node> Nodes => _nodes.Values;

    public Configuration() {
        _forwardNodeModified = (sender, args) => Modified?.Invoke(sender, args);
    }

    public void Initialize(FileInfo configFile) {
        _file = configFile;
        foreach (var t in Utils.GetDerivedTypes<Node>(Assembly.GetExecutingAssembly()).Where(t => !t.IsAbstract)) {
            if (Activator.CreateInstance(t) is not Node inst) {
                Service.Log.Error($"[Config] Failed to create an instance of {t}");
                continue;
            }
            inst.Modified += _forwardNodeModified;
            _nodes[t] = inst;
        }

        if (configFile.Exists)
            LoadFromFile(configFile);

        _saveHandler = (_, _) => SaveToFile(_file);
        Modified += _saveHandler;
    }

    public void Dispose() {
        if (_saveHandler != null)
            Modified -= _saveHandler;

        foreach (var node in _nodes.Values)
            node.Modified -= _forwardNodeModified;
        _nodes.Clear();
    }

    public T Get<T>() where T : Node => (T)_nodes[typeof(T)];
    public T Get<T>(Type derived) where T : Node => (T)_nodes[derived];

    // 契約：讀檔永不擲例外；每個節點各自 try，一個節點壞只影響它自己。
    // 有任何資料可能沒讀進來（整份讀不懂、節點中斷、版本遷移、路線截斷）就先把原檔複製成 <檔名>.bad-<時間>，
    // 因為下一次存檔會用記憶體裡的值整份蓋掉它。
    public void LoadFromFile(FileInfo file) {
        List<string> problems = [];
        try {
            var contents = File.ReadAllText(file.FullName);
            var json = JObject.Parse(contents);
            var version = (int?)json["Version"] ?? 0;
            if (json["Payload"] is JObject payload) {
                if (version < _version) {
                    Service.Log.Information($"[Config] 設定檔是第 {version} 版{(json["Version"] == null ? "（沒有 Version 欄位）" : "")}，遷移到第 {_version} 版");
                    problems.Add($"從第 {version} 版遷移");
                }
                payload = ConvertConfig(payload, version);
                var ser = BuildSerializer();
                foreach ((var t, var j) in payload) {
                    var type = Type.GetType(t);
                    var node = type != null ? _nodes.GetValueOrDefault(type) : null;
                    if (node == null || j is not JObject jObj) {
                        // 鍵＝節點型別 FullName：改過類名／namespace 的節點會落在這裡，下次存檔這個鍵就被丟掉。
                        Service.Log.Warning($"[Config] Payload 的「{t}」{(node == null ? "找不到對應的設定節點" : "不是物件")}，略過");
                        problems.Add($"略過 {t}");
                        continue;
                    }
                    try {
                        node.Deserialize(jObj, ser);
                        if (node.TakeLoadProblem() is { } lossy) {
                            Service.Log.Warning($"[Config] {t}：{lossy}");
                            problems.Add($"{type!.Name} {lossy}");
                        }
                    }
                    catch (Exception e) {
                        Service.Log.Error($"[Config] {t} 讀到一半失敗，這個節點後面的欄位用預設值，其他節點照常讀：{e}");
                        problems.Add($"{type!.Name} 讀到一半失敗");
                    }
                }
            }
            else {
                Service.Log.Warning("[Config] 設定檔沒有 Payload，全部設定用預設值");
                problems.Add("沒有 Payload");
            }
        }
        catch (Exception e) {
            Service.Log.Error($"[Config] Failed to load config from {file.FullName}: {e}");
            problems.Add("整份讀不懂");
        }
        if (problems.Count > 0)
            BackupOriginal(file, problems);
    }

    private static void BackupOriginal(FileInfo file, List<string> problems) {
        try {
            var original = File.ReadAllBytes(file.FullName);
            var dir = file.Directory!;
            // 同一份壞檔每次載入都會走到這裡（使用者沒改設定就不會被覆寫），內容相同的備份只留一份。
            foreach (var old in dir.EnumerateFiles($"{file.Name}.bad-*")) {
                if (old.Length == original.Length && File.ReadAllBytes(old.FullName).AsSpan().SequenceEqual(original)) {
                    Service.Log.Information($"[Config] 設定檔讀取有問題（{string.Join("；", problems)}），相同內容已備份過：{old.Name}");
                    return;
                }
            }
            var backup = Path.Combine(dir.FullName, $"{file.Name}.bad-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
            File.WriteAllBytes(backup, original);
            Service.Log.Information($"[Config] 設定檔讀取有問題（{string.Join("；", problems)}），原檔已備份成 {Path.GetFileName(backup)}；下次存檔會用目前的設定覆寫 {file.Name}");
        }
        catch (Exception e) {
            Service.Log.Error($"[Config] 備份原設定檔失敗：{e}");
        }
    }

    public void SaveToFile(FileInfo file) {
        try {
            var ser = BuildSerializer();
            JObject payload = [];
            foreach ((var t, var n) in _nodes) {
                var jNode = n.Serialize(ser);
                if (jNode.Count > 0) {
                    payload.Add(t.FullName!, jNode);
                }
            }
            JObject jContents = new()
            {
                { "Version", _version },
                { "Payload", payload }
            };
            File.WriteAllText(file.FullName, jContents.ToString());
        }
        catch (Exception e) {
            Service.Log.Error($"[Config] Failed to save config to {file.FullName}: {e}");
        }
    }

    public static JsonSerializer BuildSerializer() {
        var res = new JsonSerializer();
        res.Converters.Add(new StringEnumConverter());
        return res;
    }

    private static JObject ConvertConfig(JObject payload, int version) {
        // v2: backported vbm config framework
        if (version < 2) {
            var routes = payload["RouteDB"];
            var dropped = payload.Properties().Count(p => p.Name != "RouteDB");
            if (dropped > 0 || routes == null)
                Service.Log.Warning($"[Config] 第 1 版遷移只認舊的 RouteDB 鍵（{(routes == null ? "這份檔沒有" : "有")}）；其他 {dropped} 個節點被丟掉，那些設定與路線回預設值");
            payload = new() { { "visland.Gathering.GatherRouteDB", new JObject() { { "Routes", routes } } } };
        }
        // v3: turned waypoints into an array of jobjects
        if (version < 3) {
            if (payload["visland.Gathering.GatherRouteDB"] is JObject gatherRouteDB) {
                if (gatherRouteDB["Routes"] is JArray routes) {
                    JArray newRoutes = [];
                    foreach (var route in routes) {
                        if (route is JObject routeObj && routeObj["Waypoints"] is JArray waypoints) {
                            var newWaypoints = new JArray();
                            var droppedWaypoints = waypoints.Count(w => w is not JArray);
                            if (droppedWaypoints > 0)
                                Service.Log.Warning($"[Config] 第 3 版遷移：路線「{routeObj["Name"]}」有 {droppedWaypoints} 個航點不是舊的陣列格式，被丟掉");
                            foreach (var waypoint in waypoints) {
                                if (waypoint is JArray oldWaypoint) {
                                    var newWaypoint = new JObject
                                    {
                                        { "X", oldWaypoint.Count > 0 ? oldWaypoint[0] : 0 },
                                        { "Y", oldWaypoint.Count > 1 ? oldWaypoint[1] : 0 },
                                        { "Z", oldWaypoint.Count > 2 ? oldWaypoint[2] : 0 },
                                        { "Radius", oldWaypoint.Count > 3 ? oldWaypoint[3] : 0 },
                                        { "InteractWithName", oldWaypoint.Count > 4 ? oldWaypoint[4] : "" },
                                        { "Movement", oldWaypoint.Count > 5 ? oldWaypoint[5] : "" },
                                        { "InteractWithOID", oldWaypoint.Count > 6 ? oldWaypoint[6] : 0 },
                                        { "showInteractions", oldWaypoint.Count > 7 ? oldWaypoint[7] : false },
                                        { "Interaction", oldWaypoint.Count > 8 ? oldWaypoint[8] : "" },
                                        { "EmoteID", oldWaypoint.Count > 9 ? oldWaypoint[9] : 0 },
                                        { "ActionID", oldWaypoint.Count > 10 ? oldWaypoint[10] : 0 },
                                        { "ItemID", oldWaypoint.Count > 11 ? oldWaypoint[11] : 0 },
                                        { "showWaits", oldWaypoint.Count > 12 ? oldWaypoint[12] : false },
                                        { "WaitTimeMs", oldWaypoint.Count > 13 ? oldWaypoint[13] : 0 },
                                        { "WaitForCondition", oldWaypoint.Count > 14 ? oldWaypoint[14] : "" },
                                        { "Pathfind", oldWaypoint.Count > 15 ? oldWaypoint[15] : false },
                                        { "MobID", oldWaypoint.Count > 16 ? oldWaypoint[16] : 0 },
                                        { "QuestID", oldWaypoint.Count > 17 ? oldWaypoint[17] : 0 },
                                        { "RouteName", oldWaypoint.Count > 18 ? oldWaypoint[18] : "" },
                                        { "ChatCommand", oldWaypoint.Count > 19 ? oldWaypoint[19] : "" },
                                    };
                                    newWaypoints.Add(newWaypoint);
                                }
                            }
                            routeObj["Waypoints"] = newWaypoints;
                            newRoutes.Add(routeObj);
                        }
                    }
                    gatherRouteDB["Routes"] = newRoutes;
                }
            }
        }
        return payload;
    }
}
