using System.Linq;
using Newtonsoft.Json;
using visland.Gathering;
using visland.Helpers;

namespace visland.IPC;

/// <summary>visland 對外提供的 IPC 端點。</summary>
/// <remarks>🔴 每一個端點的方法體都包在 <see cref="IpcFrameworkGate"/> 裡：CallGate 是直接方法呼叫，這些碼跑在**呼叫端的執行緒**上，而它們動的是 <see cref="GatherRouteExec"/> 的狀態
/// （裸 <c>List&lt;T&gt;</c> 的路徑點、原生 addon 指標、原生 hook 的開關），而 <c>GatherRouteExec.Update</c> 每一幀都在讀同一批東西。閘門的細節與逾時語意寫在
/// <see cref="IpcFrameworkGate"/> 的註解裡；呼叫端已經在主執行緒上時行為逐字不變。</remarks>
public class VislandIPC {
    public VislandIPC() {
        // ⚠️ 查詢類端點逾時回的是「保守側」而不是字面上的 false：主執行緒卡住超過五秒時
        //    回報「路線還在跑」，呼叫端會繼續等；回 false 會讓它以為 visland 空了，
        //    然後兩邊同時開始自動化。
        Service.Interface.GetIpcProvider<bool>("visland.IsRouteRunning")
            .RegisterFunc(() => IpcFrameworkGate.Get("visland.IsRouteRunning",
                () => Service.RouteExec.CurrentRoute != null && !Service.RouteExec.Paused, true));
        // 反過來，「有沒有暫停」逾時回 false（還沒暫停）才保守：呼叫端會再送一次
        //    SetRoutePaused(true)，而那個動作是冪等的。
        Service.Interface.GetIpcProvider<bool>("visland.IsRoutePaused")
            .RegisterFunc(() => IpcFrameworkGate.Get("visland.IsRoutePaused",
                () => Service.RouteExec.Paused, false));
        Service.Interface.GetIpcProvider<bool, object>("visland.SetRoutePaused")
            .RegisterAction(state => IpcFrameworkGate.Run("visland.SetRoutePaused",
                () => Service.RouteExec.Paused = state));
        Service.Interface.GetIpcProvider<object>("visland.StopRoute")
            .RegisterAction(() => IpcFrameworkGate.Run("visland.StopRoute",
                () => Service.RouteExec.Finish()));
        Service.Interface.GetIpcProvider<string, bool, object>("visland.StartRoute")
            .RegisterAction((route, once) => {
                // 解壓縮與反序列化是純 CPU、碰不到遊戲記憶體，刻意留在呼叫端的執行緒上跑：
                // 不佔主執行緒的幀，格式錯誤擲出的例外型別也與加閘門前完全一樣。
                var (_, json) = Utils.FromCompressedBase64(route);
                var parsed = JsonConvert.DeserializeObject<GatherRouteDB.Route>(json);
                if (parsed != null)
                    IpcFrameworkGate.Run("visland.StartRoute", () => Service.RouteExec.Start(parsed, 0, true, !once));
            });
        // 🔴 這一支整段都要進閘門：Items 的 getter 與 Gather() 都會解參原生 addon 指標並送
        //    FireCallback，在別的執行緒上做等於在讀遊戲每幀重建的記憶體。
        Service.Interface.GetIpcProvider<uint, object>("visland.GatherItem")
            .RegisterAction(itemId => IpcFrameworkGate.Run("visland.GatherItem", () => {
                var item = Service.RouteExec.GatheringAM?.Items.FirstOrDefault(x => x.ItemID == itemId);
                item?.Gather();
            }));
    }
}
