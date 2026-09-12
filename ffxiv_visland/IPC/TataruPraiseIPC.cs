using Dalamud.Plugin.Ipc.Exceptions;
using System;
using System.Reflection;

namespace visland.IPC;

/// <summary>單向橋接到「塔塔露誇獎」(TataruPraise)：採集路線整條跑完、或是因為連續錯誤被停掉時請它念一句。</summary>
/// <remarks>🔴 <b>只用 Dalamud 原生 CallGate 的字串契約。</b>契約名逐字取自 TataruPraise 的<c>IpcContract.cs</c> 與 <c>PraiseCategory.cs</c>；CallGate 是純字串比對，<b>名字打錯不會有任何錯誤訊息</b>，只會永遠拿到「這個頻道沒有人註冊」——靜默斷線。所以字串都寫成常數，不散在呼叫點上。
/// 這裡仍然過一次 <c>RunOnFrameworkThread</c> 當安全網：它在<b>已經是</b> framework 執行緒時就地同步執行，所以現況一幀都沒多花。
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響路線的任何流程，不重試，也不會因此做任何遊戲操作。
/// </remarks>
internal static class TataruPraiseIPC {
    /// <summary><c>Func&lt;string, bool&gt;</c>：<b>指定的那個情境</b>現在出得了聲嗎（總開關開著＋這個情境沒被關掉＋這個情境至少有一句已合成的語音）。</summary>
    /// <remarks>🔴 閘門要問的是這一個，<b>不是</b> <c>TataruPraise.IsAvailable</c>：後者問的是「整池<b>有某個情境</b>播得出來」，於是「別的情境有語音、路線這個一句都沒有」時照樣通過，接著 <c>Praise</c> 回 <c>false</c>——呼叫端就分不出「不能出聲」與「這次剛好沒出聲」。
    /// 🔴 舊版 TataruPraise 沒有註冊這個端點，<c>InvokeFunc</c> 會擲 <c>IpcNotReadyError</c>，剛好落進既有的 catch＝安靜不出聲，這是正確的 fail-safe。<b>失敗時絕不可以退回去叫 <c>IsAvailable</c></b>——那樣就把這個端點的意義整個抵銷掉了。
    /// </remarks>
    private const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句念。</summary>
    private const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 「採集路線整條跑完」的情境鍵。對方端的常數是 <c>PraiseCategory.RouteDone</c>，逐字相同。
    /// </summary>
    /// <remarks>
    /// ⚠️ TataruPraise 拿這個字串當 <c>pool.json</c> 的鍵，<b>對不上就靜默不出聲</b>
    /// （它會在記錄檔印一次「未知情境」）。
    /// </remarks>
    private const string CategoryRouteDone = "路線跑完";

    /// <summary>
    /// 「路線被錯誤停掉」用的情境鍵。對方端的常數是 <c>PraiseCategory.NeedHelp</c>，逐字相同。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意<b>不</b>跟 <see cref="CategoryRouteDone"/> 共用：「跑完了」跟「卡住被迫停下」
    /// 念同一句話等於沒講。這個鍵在 TataruPraise 的語意就是「自動化卡住了，需要前輩過來看一下」。
    /// </remarks>
    private const string CategoryNeedHelp = "需要幫忙";

    /// <summary>
    /// 路線<b>走到最後一個點而且沒開循環</b>時叫這個
    /// （閘門是 <see cref="Gathering.GatherRouteDB.TataruPraiseOnRouteDone"/>）。
    /// </summary>
    /// <param name="reason">寫進記錄用的來源描述。</param>
    internal static void TryPraiseRouteDone(string reason) {
        if (!Service.Config.Get<Gathering.GatherRouteDB>().TataruPraiseOnRouteDone) return;
        Send(CategoryRouteDone, reason);
    }

    /// <summary>
    /// 路線<b>因為連續錯誤被自動停掉</b>時叫這個
    /// （閘門是 <see cref="Gathering.GatherRouteDB.TataruPraiseOnErrorStop"/>）。
    /// </summary>
    /// <param name="reason">寫進記錄用的來源描述。</param>
    internal static void TryPraiseErrorStop(string reason) {
        if (!Service.Config.Get<Gathering.GatherRouteDB>().TataruPraiseOnErrorStop) return;
        Send(CategoryNeedHelp, reason);
    }

    /// <summary>把整段 IPC 交易排到 framework 執行緒上跑；使用者的開關已經在呼叫端判過了。</summary>
    /// <remarks>
    /// 🔴 <b>刻意不等它跑完</b>：呼叫點都在「路線已經結束、正在收尾」的路徑上，
    /// 沒有任何一處需要等念完才能往下走。
    /// </remarks>
    private static void Send(string category, string reason) {
        try {
            _ = Service.Framework.RunOnFrameworkThread(() => SendOnFramework(category, reason));
        }
        catch (Exception ex) {
            // 外掛正在卸載時排程器可能已經收掉了。留一行線索就好。
            Service.Log.Information($"[TataruPraise] 排程失敗（{reason}）：{ex.Message}");
        }
    }

    /// <summary>真正打 IPC 的那一段，保證在 framework 執行緒上。</summary>
    private static void SendOnFramework(string category, string reason) {
        try {
            // 先問 IsAvailableFor：對方的總開關關著、這個情境被使用者關掉、或這個情境一句已合成的
            // 都沒有，就不要浪費它的冷卻。
            if (!Service.Interface.GetIpcSubscriber<string, bool>(TagIsAvailableFor).InvokeFunc(category))
                return;

            var accepted = Service.Interface.GetIpcSubscriber<string, bool>(TagPraise).InvokeFunc(category);
            // Information 級：這是「使用者說沒出聲」時唯一問得出真相的一行。
            Service.Log.Information($"[TataruPraise] {reason}：Praise(「{category}」) 回傳 {accepted}。");
        }
        catch (IpcNotReadyError) {
            // 對方沒安裝／沒載入。完全正常的狀態，刻意不寫記錄——沒裝的人每條路線跑完都會走到這裡。
        }
        catch (TargetInvocationException ex) {
            // 🔴 CallGate 走 Func.DynamicInvoke，**提供端自己擲的例外**一律被包成這個型別，
            //    IpcError 那一族一個都攔不到。不攔它的話這裡會把例外丟回 framework 執行緒上。
            Service.Log.Information($"[TataruPraise] 對方端擲出例外（{reason}）：{ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex) {
            Service.Log.Information($"[TataruPraise] 呼叫失敗（{reason}）：{ex.Message}");
        }
    }
}
