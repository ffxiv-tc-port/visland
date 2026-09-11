using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace visland.IPC;

/// <summary>
/// IPC 端點的「遊戲主執行緒閘門」。
/// </summary>
/// <remarks>
/// 🔴🔴 為什麼需要這一層：Dalamud 的 CallGate 是**直接方法呼叫**，
/// 提供端的碼跑在**呼叫端的執行緒**上。別的外掛（AutoRetainer、SomethingNeedDoing、
/// AutoDuty…）從自己的背景工作、<c>Task.Run</c>、或任何非 framework 執行緒打過來時，
/// visland 這一側就會在那條執行緒上動路線執行器的狀態，而
/// <see cref="visland.Gathering.GatherRouteExec.Update"/> 每一幀都在讀同一批東西：
/// <list type="bullet">
/// <item><c>CurrentRoute.Waypoints</c> 是裸 <c>List&lt;T&gt;</c>。<c>Start</c>／<c>Finish</c>
/// 都會對它 <c>RemoveAll(...)</c>，而 framework 執行緒同一時間在讀 <c>Waypoints.Count</c>
/// 並用 <c>CurrentWaypoint</c> 索引 —— 並行改動時的失敗形式不是「拿到舊值」而是
/// <c>ArgumentOutOfRangeException</c>／清單內部結構壞掉。</item>
/// <item><c>visland.GatherItem</c> 會解參原生 addon 指標並送
/// <c>FireCallback</c>（<c>GatheringAddon.Gather</c>）。那是遊戲主執行緒每幀重建的記憶體，
/// 讀到一半被換掉就是 AccessViolationException —— 而 AVE 在 .NET Core 是
/// corrupted-state exception，<c>try</c>/<c>catch</c> 攔不到，整個遊戲直接崩掉。</item>
/// <item><c>Start</c>／<c>Finish</c> 還會翻 <c>OverrideCamera</c>／<c>OverrideMovement</c>
/// 的 <c>Enabled</c>（會安裝／卸下原生 hook）並呼叫 <c>CompatModule.RestoreChanges</c>
/// 與 vnavmesh 的 IPC。</item>
/// </list>
/// <br/>
/// 🔑 所以凡是碰得到路線執行器狀態的端點，一律把**整個方法體**交回主執行緒執行，
/// 不是只有第一行檢查 —— 這樣連下游 helper 也一起被覆蓋，不必逐一追。
/// <br/><br/>
/// 📌 <b>已經在主執行緒上呼叫時行為逐字不變</b>：直接就地執行，不配置 Task、
/// 不改變例外型別、不多花任何一幀。絕大多數消費端（別的外掛在自己的
/// <c>Framework.Update</c> 或 <c>TaskManager</c> 裡呼叫）走的就是這條路。
/// <br/><br/>
/// ⚠️ 逾時的處置：等主執行緒最多 <see cref="TimeoutMs"/> 毫秒。逾時就回該端點的
/// 「保守值」—— 對查詢類端點是「假設 visland 還忙著」那一側，讓呼叫端繼續等而不是
/// 搶著開始自己的自動化。同時用 <see cref="Interlocked"/> 把還沒開始跑的工作標成放棄，
/// 避免「呼叫端已經拿到回值走人了，五秒後路線才真的被啟動」這種無人值守亂跑的形狀。
/// <br/><br/>
/// 🔴 用 <c>RunOnFrameworkThread</c> 不是 <c>Framework.Run</c>：前者在已經是主執行緒時
/// 就地執行，同步等它不會死結；後者一律 <c>StartNew</c>，同步等會死結。
/// </remarks>
internal static class IpcFrameworkGate {
    /// <summary>等主執行緒的上限。超過就當作「現在做不到」。</summary>
    internal const int TimeoutMs = 5000;

    private const int StatePending = 0;
    private const int StateRunning = 1;
    private const int StateAbandoned = 2;

    /// <summary>
    /// 有回傳值的端點。<paramref name="unavailable"/> 是逾時時要回的保守值。
    /// </summary>
    internal static T Get<T>(string endpoint, Func<T> body, T unavailable) {
        if (Service.Framework.IsInFrameworkUpdateThread) return body();

        if (IsUnloading(endpoint)) return unavailable;

        var state = StatePending;
        var task = Service.Framework.RunOnFrameworkThread(() => {
            // 呼叫端已經逾時走人了就什麼都不做。
            if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending) return unavailable;
            return body();
        });
        // WaitAny 對已經失敗的工作也回 0（不擲），交給 GetResult 原樣重擲原始例外，
        // 這樣呼叫端看到的例外型別與沒有這層閘門時完全一樣（不會變成 AggregateException）。
        if (Task.WaitAny([task], TimeoutMs) == 0) return task.GetAwaiter().GetResult();
        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
        return unavailable;
    }

    /// <summary>沒有回傳值的端點。</summary>
    internal static void Run(string endpoint, Action body) {
        if (Service.Framework.IsInFrameworkUpdateThread) {
            body();
            return;
        }

        if (IsUnloading(endpoint)) return;

        var state = StatePending;
        var task = Service.Framework.RunOnFrameworkThread(() => {
            if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending) return;
            body();
        });
        if (Task.WaitAny([task], TimeoutMs) == 0) {
            task.GetAwaiter().GetResult();
            return;
        }
        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
    }

    /// <summary>同一個端點的逾時訊息重印間隔。</summary>
    private const long TimeoutLogIntervalMs = 10000;

    /// <summary>節流表上限，避免端點名意外發散時無限成長。</summary>
    private const int MaxTrackedTimeoutKeys = 128;

    private static readonly Dictionary<string, long> TimeoutLogTimes = [];

    /// <summary>
    /// 🔴 Dalamud 卸載期的閘門旁路：<c>Framework.RunOnFrameworkThread</c> 在
    /// <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端執行緒</b>執行 body
    /// （<c>Dalamud/Game/Framework.cs</c> 的 <c>IsInFrameworkUpdateThread || IsFrameworkUnloading</c>），
    /// 等於這一層完全失效、原生記憶體存取退回未保護狀態。
    /// 🔑 所以卸載期一律直接回該端點原本的「不可用」值：那一瞬間功能失效可以接受
    /// （遊戲要關了），卸載期的 AccessViolationException 不行 —— 使用者看到的是崩潰。
    /// 📌 已經在 framework 執行緒上時不受影響（那本來就是安全的執行緒），
    /// 所以外掛自己在 <c>Dispose</c> 裡的同步呼叫行為逐字不變。
    /// </summary>
    private static bool IsUnloading(string endpoint) {
        if (!Service.Framework.IsFrameworkUnloading || Service.Framework.IsInFrameworkUpdateThread) return false;
        if (ShouldLogTimeout(endpoint + "/卸載期")) {
            Service.Log.Information($"[visland IPC 閘門] {endpoint} 在 Dalamud 卸載期從別的執行緒被呼叫，已回傳保守值。卸載期的 RunOnFrameworkThread 會就地在呼叫端執行緒執行，閘門保護不了原生記憶體存取；此時功能失效可以接受，崩潰不行。");
        }
        return true;
    }

    /// <summary>
    /// 自帶的節流：首次必放行，之後每 <see cref="TimeoutLogIntervalMs"/> 毫秒放行一次。
    /// 🔴 這裡刻意**不用** ECommons 的 <c>EzThrottler</c>：它是整個外掛共用的靜態 Dictionary
    /// 且零同步，而這條路徑跑在呼叫端的執行緒上，並行插入弄壞的是整張表 —— 連帶弄壞外掛裡
    /// 所有模組的節流。所以自帶字典＋自己的鎖。
    /// 🔴 鎖內只碰字典 —— 不寫 log、不做 I/O、不呼叫任何別的外掛。
    /// </summary>
    private static bool ShouldLogTimeout(string key) {
        var now = Environment.TickCount64;
        lock (TimeoutLogTimes) {
            if (TimeoutLogTimes.TryGetValue(key, out var last) && now - last < TimeoutLogIntervalMs) return false;
            if (TimeoutLogTimes.Count >= MaxTrackedTimeoutKeys && !TimeoutLogTimes.ContainsKey(key)) TimeoutLogTimes.Clear();
            TimeoutLogTimes[key] = now;
            return true;
        }
    }

    /// <summary>要使用者回報的診斷寫 Information（使用者的 LogLevel 收得到，且不會被 Debug 的數十萬行淹沒）。</summary>
    private static void ReportTimeout(string endpoint, bool abandoned) {
        if (!ShouldLogTimeout(endpoint)) return;
        var outcome = abandoned
            ? "工作還沒開始就被取消，什麼都沒做"
            : "工作已經開始執行，會照常跑完（呼叫端拿到的回值不代表它沒發生）";
        Service.Log.Information($"[visland IPC 閘門] {endpoint} 等待遊戲主執行緒超過 {TimeoutMs} 毫秒，已回傳保守值。{outcome}。通常代表遊戲正在讀取畫面或嚴重掉幀；若持續出現請回報。");
    }
}
