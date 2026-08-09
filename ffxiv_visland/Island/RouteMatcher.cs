using System.Collections.Generic;
using visland.Gathering;

namespace visland.Island;

// 把「已儲存的採集路線」對上「無人島材料」。
//
// 🔴 只能用座標圓比對 —— 另外三條看起來更直接的路都會**靜默**失敗
//    (以使用者手上 23 條路線 / 394 個路徑點實測):
//    ① 路線名稱是使用者自己取的,實測整批是簡體中文用語,跟任何遊戲資料表都對不上;
//    ② Waypoint.InteractWithName 存的是遊戲物件名(英/法文),不是材料名;
//    ③ Waypoint.InteractWithOID 全島只有兩個值(2012985 佔 257 筆、2013159 佔 20 筆),
//       所有採集點共用同一個 OID,分不出採的是什麼;
//    ④ 394 個路徑點裡有 298 個 ZoneID = 0,拿區域 id 過濾會濾掉四分之三。
//
// 座標的依據:MJIGatheringItem 的 X / Y 兩欄實際上是世界座標的 X / Z(表上叫 Y),
// 半徑就是同列的 Radius。離線用上述 23 條路線驗過:每條以材料命名的路線,
// 名稱裡的材料都落在命中集合內,而「Garden」(耕地,不是採集)那條 20 個路徑點全部落空
// —— 有正樣本也有負樣本。
//
// ⚠️ 已知限制:採集圓大量重疊(例如石材/石灰岩/大理石都在 (-22, -46) 半徑 80,
//    石英的半徑更是 200),所以一條路線會命中它實際上不採的材料。
//    這裡不猜、不過濾,照實把覆蓋集合顯示出來。
public sealed class RouteCoverage {
    public GatherRouteDB.Route Route = null!;
    public readonly Dictionary<uint, int> HitsByPouch = [];
    public bool Approximate; // 這條路線沒有互動路徑點,退而用全部路徑點比對

    // 這條路線覆蓋到幾種「目前缺的」材料。由 UI 每幀重算一次填進來 ——
    // 不要在排序比較器裡臨時算,那會變成 (列數 x log(路線數) x 材料數) 的每幀成本。
    public int ShortageHits;

    public int MaterialCount => HitsByPouch.Count;
}

public static class RouteMatcher {
    public static List<RouteCoverage> Compute(IReadOnlyList<GatherRouteDB.Route> routes) {
        List<(uint Pouch, float X, float Z, float R2)> spots = [];
        foreach (var info in MaterialSources.All) {
            var g = info.Gather;
            if (g == null || g.Radius <= 0)
                continue;
            spots.Add((info.PouchId, g.X, g.Z, g.Radius * g.Radius));
        }

        List<RouteCoverage> result = [];
        if (spots.Count == 0)
            return result;

        foreach (var route in routes) {
            var interactions = 0;
            foreach (var wp in route.Waypoints)
                if (wp.InteractWithOID != 0)
                    ++interactions;

            var cov = new RouteCoverage { Route = route, Approximate = interactions == 0 };
            foreach (var wp in route.Waypoints) {
                if (!cov.Approximate && wp.InteractWithOID == 0)
                    continue;
                var x = wp.Position.X;
                var z = wp.Position.Z;
                foreach (var s in spots) {
                    var dx = x - s.X;
                    var dz = z - s.Z;
                    if (dx * dx + dz * dz <= s.R2)
                        cov.HitsByPouch[s.Pouch] = cov.HitsByPouch.GetValueOrDefault(s.Pouch) + 1;
                }
            }

            if (cov.HitsByPouch.Count > 0)
                result.Add(cov);
        }
        return result;
    }
}
