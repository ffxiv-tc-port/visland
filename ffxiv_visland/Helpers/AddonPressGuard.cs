using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Threading;

namespace visland.Helpers;

/// <summary>
/// 「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。
/// visland 對 addon 的所有按法(<see cref="AtkCallback.Fire(string, bool, string, int[])"/>、
/// <c>AddonUtils.ClickButton</c>、以及兩處直呼 <c>FireCallback</c> 的地方)都要先問過
/// <see cref="TryBeginPress(string, nint, string)"/>。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:「按下即關」的窗被按下之後有
/// <b>「正在關閉中」的幾幀</b>,這段期間 <c>GetAddonByName</c> 仍然回得到實例、
/// <c>IsVisible</c> 與 <c>UldManager.LoadedState == Loaded</c> 也都還成立
/// (＝ <c>AddonUtils.IsAddonReady</c> 三關全過、擋不住這個窗口)。此時再對它送
/// callback／輸入事件就是原生 AccessViolation(C0000005)。AVE 在 .NET Core 是
/// corrupted-state exception,<c>try</c>/<c>catch</c> 完全攔不到
/// (<c>SpiritbondManager.ConfirmMateriaDialog</c> 外面那圈 <c>try/catch</c> 對它無效),
/// 遊戲當場關閉 —— <b>唯一的防護是「不要送第二次」,不是「送了再接住」</b>。
/// </para>
/// <para>
/// 🔴 節流<b>不是</b>防護:<c>SpiritbondManager._nextRetry</c> 與
/// <c>PurificationManager._nextRetry</c> 記的是「上一次動作在哪個<b>時刻</b>」,
/// 不是「這扇窗已經按過」。一次 ≥ 節流長度的幀停頓(讀圖、掉幀)就會讓下一輪
/// 正好落在關閉中的第 1 幀。
/// </para>
/// <para>
/// 🔴 <b>位址只做等值比較,永遠不解參考。</b> 位址可能被下一扇窗重用,所以要搭配
/// 「見過生命週期結束」的狀態轉換(<see cref="AddonEvent.PreFinalize"/> ／
/// <see cref="AddonEvent.PostSetup"/>)與輪詢掃描,再加逃生口兜底。
/// </para>
/// <para>
/// 🔑 <b>粒度＝(窗名, 位址, 按法)</b>。「一扇窗只准按一次」照抄會弄壞「對同一扇窗連送不同參數」
/// 的正常流程,所以按法(<c>pressKey</c>)要由呼叫端明確給。只有<b>回答一次即終結</b>的窗
/// (<see cref="SingleAnswerAddons"/>)才把所有按法併成同一個 key ——
/// 那種窗按第二下的對象一定是關閉中的它自己。
/// </para>
/// </remarks>
internal static unsafe class AddonPressGuard {
    /// <summary>
    /// 單答終結窗(按下即關)的逃生口。遠大於關閉所需的幀數,只在「按了但窗沒收也沒重建」
    /// 這種異常狀況下放行補按,免得呼叫端卡到 <c>TaskManager</c> 的 20 秒逾時把整條佇列清掉。
    /// </summary>
    internal const int DefaultEscapeFrames = 90;

    /// <summary>
    /// 多次互動窗(按一次翻一頁／採一格,窗<b>不會</b>因為被按而消失)的逃生口。
    /// 走逃生口是<b>常態</b>,所以只寫 Debug、不洗版。
    /// 判斷依據:關閉中的危險窗口 &lt; 10 幀,15 幀不落在裡面;每次 +0.25 秒幾乎無感。
    /// </summary>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>「我們主動要求這扇窗關閉」的紀錄用的按法名稱(見 <see cref="MarkClosing"/>)。</summary>
    internal const string ClosePressKey = "Close";

    /// <summary>
    /// 掃描 addon 清單時的索引上限。<c>GetAddonByName</c> 的 index 是同名視窗的連號(1 起算),
    /// 掃到第一個空的就停,所以這個上限實務上碰不到。
    /// </summary>
    private const int MaxAddonIndex = 32;

    /// <summary>
    /// 「回答一次即終結」的窗:按下去就開始關閉,所以<b>任何</b>按法對同一個位址都只准一次。
    /// ⚠️ 這是分類表不是按點清單 —— <c>SelectYesno</c> visland 目前沒有按它,
    /// 列在這裡是為了日後有人加按點時預設就是對的。
    /// </summary>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal) {
        "MaterializeDialog",    // 精製確認框:按 0 之後即關
        "ContextIconMenu",      // 無人島 HUD 的環狀選單:選一項或送 -1 之後即關
        "PurifyResult",         // 精選結果框:按 0 之後即關
        "SelectYesno",
    };

    /// <summary>
    /// 多次互動窗:按一次做一件事、窗本身<b>不會</b>因此消失,所以逃生口用
    /// <see cref="RoutineRePressEscapeFrames"/> 而不是 <see cref="DefaultEscapeFrames"/>。
    /// </summary>
    private static readonly HashSet<string> RoutineAddons = new(StringComparer.Ordinal) {
        "MJIHud",       // 常駐 HUD,CompatModule 會每幀重發直到 CurrentMode 變 1
        "Materialize",  // 精製主視窗:按一次抽一顆魔晶石,窗留著
        "Gathering",    // 採集視窗:按一次採一格,窗留到採完才由遊戲自己收
    };

    /// <summary>位址只存來做等值比較,<b>永遠不解參考</b>。</summary>
    private readonly record struct PressRecord(nint Address, long Frame, int EscapeFrames);

    private static readonly Dictionary<string, Dictionary<string, PressRecord>> PressedByAddon = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LogNextFrame = new(StringComparer.Ordinal);

    // 可重用緩衝:沒有任何窗被記著時,每幀的成本就是一次整數比較,不配置任何東西。
    private static readonly List<string> NamesBuf = [];
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly List<string> SweepKeysBuf = [];
    private static readonly List<string> LifecycleKeysBuf = [];

    /// <summary>
    /// 守衛自己的時鐘。
    /// <para>🔴🔴 <b>絕對不能用 <c>UiBuilder.FrameCount</c>。</b> 那個計數器的遞增點在
    /// <c>UiBuilder.OnDraw</c> 的三個「隱藏 UI 就 return」之後(過場動畫、使用者按隱藏 UI 熱鍵、
    /// GPose,三個開關預設全開)⇒ 過場中它<b>完全不前進</b>,而按下點走的是遊戲更新迴圈、
    /// 照常每幀被叫到 ⇒ 逃生口永不到期,多次互動窗會停在第一步。</para>
    /// <para>🔴 遞增點在 <see cref="OnFrameworkUpdate"/> 的<b>第一行</b>,前面不可以有任何條件 ——
    /// 放到 early return 後面的話,沒有窗被記著時時鐘就停住,等於沒修。</para>
    /// </summary>
    private static long frameCount;

    /// <summary>
    /// 🔴 用 <see cref="Interlocked.CompareExchange(ref int, int, int)"/> 而不是 <c>bool</c>:
    /// 重複訂閱不是「沒效果」,是<b>一個 tick 前進 2</b> ＝ 所有逃生口對半砍,
    /// 會把補按往危險窗口推。
    /// </summary>
    private static int clockSubscribed;

    /// <summary>
    /// 在 <c>Service.Init</c> 一拿到服務就叫一次,讓時鐘排在本外掛 <c>Framework.Update</c>
    /// 多播委派的<b>最前面</b>。
    /// <para>🔴 同一個外掛內部的 <c>Framework.Update</c> <b>沒有</b> per-handler 例外隔離
    /// (整條多播委派包在單一 try/catch),排在前面的 handler 擲例外會讓後面所有 handler
    /// 那個 tick 完全不被呼叫 —— 時鐘停住就等於逃生口失效。</para>
    /// </summary>
    internal static void Initialize() => EnsureClock();

    /// <summary>
    /// 問「現在可以對這扇窗送這個按法嗎」。可以就<b>就地登記</b>並回 <c>true</c>;
    /// 回 <c>false</c> 代表「這一輪沒按到,下一輪再來」——
    /// 與既有「addon 還沒出現」走同一條路徑,不改變任何呼叫端的控制流。
    /// </summary>
    /// <param name="addonName">視窗名稱(生命週期監聽與掃描都靠它)。</param>
    /// <param name="addon">視窗指標,<b>只取位址做等值比較</b>。</param>
    /// <param name="pressKey">按法名稱(參數組的代稱)。單答終結窗會忽略它、併成同一個 key。</param>
    internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "")
        => TryBeginPress(addonName, (nint)addon, pressKey);

    /// <inheritdoc cref="TryBeginPress(string, AtkUnitBase*, string)"/>
    internal static bool TryBeginPress(string addonName, nint address, string pressKey = "") {
        // 🔴 時鐘的後援訂閱點,放在所有 early return 之前(CompareExchange 保證只會生效一次)。
        EnsureClock();

        if (address == 0 || string.IsNullOrEmpty(addonName))
            return false;

        var singleAnswer = SingleAnswerAddons.Contains(addonName);
        if (singleAnswer && pressKey != ClosePressKey)
            pressKey = string.Empty;

        var escapeFrames = EscapeFramesFor(addonName);
        var routine = escapeFrames <= RoutineRePressEscapeFrames;
        var frame = frameCount;

        EnsureWatching(addonName);

        if (!PressedByAddon.TryGetValue(addonName, out var presses)) {
            presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }
        else {
            if (FindBlocking(presses, address, pressKey, singleAnswer, frame, out var blockingKey)) {
                // 🔴 這就是崩潰的那一幀。
                LogHold(addonName, address, pressKey, blockingKey, routine);
                return false;
            }

            if (presses.TryGetValue(pressKey, out var same) && same.Address == address) {
                // 同一個按法對同一扇窗按過、已經冷掉(超過逃生口)而位址還是同一個:
                // 視為那次沒生效(或這是重用了同一塊記憶體、且沒觸發 PostSetup 的另一扇窗),放行補按。
                var waited = frame - same.Frame;
                if (routine) {
                    if (LogThrottle($"RoutineRelease-{addonName}", 600))
                        Service.Log.Debug($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                          $"按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次。");
                }
                else if (LogThrottle($"Release-{addonName}", 600)) {
                    Service.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                            $"按下後 {waited} 幀既沒有被銷毀也沒有重新建立,判定為「上一次按下沒生效」" +
                                            "而不是「正在關閉」,解除封鎖讓呼叫端重試。");
                }
            }
        }

        presses[pressKey] = new PressRecord(address, frame, escapeFrames);
        // 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
        Service.Log.Information($"[按窗診斷] plugin=visland addon={addonName} addr=0x{address:X} key={pressKey}");
        return true;
    }

    /// <summary>
    /// 登記「我們剛剛主動要求這扇窗關閉」(例如用 toggle 型的通用動作把它關掉)。
    /// 之後對<b>同一個位址</b>的任何按法都會被擋到它真的消失,或撐過
    /// <see cref="DefaultEscapeFrames"/> 為止。
    /// <para>這是 visland 特有的必要補強:<c>Repair</c> 與 <c>Materialize</c> 都是靠通用動作
    /// toggle 關閉的,窗不會因為「被按」而關 —— 沒有這一步的話,「上一輪把窗關掉、
    /// 這一輪對淡出中的它再按一次」那條路徑上,按下紀錄早就冷掉了,擋不住。</para>
    /// </summary>
    internal static void MarkClosing(string addonName, nint address) {
        EnsureClock();

        if (address == 0 || string.IsNullOrEmpty(addonName))
            return;

        EnsureWatching(addonName);

        if (!PressedByAddon.TryGetValue(addonName, out var presses)) {
            presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }

        presses[ClosePressKey] = new PressRecord(address, frameCount, DefaultEscapeFrames);
    }

    /// <summary>
    /// 每幀的時鐘與解除軌之一:輪詢 addon 清單,把「位址已經不在清單裡」的紀錄清掉。
    /// <para>另一軌是 <see cref="AddonEvent.PreFinalize"/> ／ <see cref="AddonEvent.PostSetup"/>
    /// (見 <see cref="EnsureWatching"/>)。兩軌都要:單靠輪詢有「同一塊記憶體被新窗重用、
    /// 而新窗剛好也叫同一個名字」的誤擋缺口;單靠事件則會漏掉「事件沒送達」的情形。</para>
    /// </summary>
    private static void OnFrameworkUpdate(IFramework framework) {
        frameCount++;   // 🔴 第一行。前面不可以有任何條件。

        if (PressedByAddon.Count == 0)
            return;

        NamesBuf.Clear();
        foreach (var name in PressedByAddon.Keys)
            NamesBuf.Add(name);

        foreach (var name in NamesBuf) {
            if (!PressedByAddon.TryGetValue(name, out var presses))
                continue;

            PresentBuf.Clear();
            for (var i = 1; i <= MaxAddonIndex; i++) {
                var live = Service.GameGui.GetAddonByName(name, i).Address;
                if (live == 0)
                    break;
                PresentBuf.Add(live);
            }

            SweepKeysBuf.Clear();
            foreach (var (key, rec) in presses) {
                if (!PresentBuf.Contains(rec.Address))
                    SweepKeysBuf.Add(key);
            }
            foreach (var key in SweepKeysBuf)
                presses.Remove(key);

            if (presses.Count == 0)
                PressedByAddon.Remove(name);
        }
    }

    /// <summary>外掛卸載時拆乾淨(本 pin 其實會自動拆,但顯式拆掉才不會留下半死的狀態)。</summary>
    internal static void ForceTeardown() {
        if (Interlocked.Exchange(ref clockSubscribed, 0) == 1 && Service.Framework != null)
            Service.Framework.Update -= OnFrameworkUpdate;

        if (Service.AddonLifecycle != null) {
            foreach (var (addonName, handler) in Watchers) {
                Service.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
                Service.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
            }
        }

        Watchers.Clear();
        PressedByAddon.Clear();
        LogNextFrame.Clear();
    }

    private static int EscapeFramesFor(string addonName)
        => RoutineAddons.Contains(addonName) ? RoutineRePressEscapeFrames : DefaultEscapeFrames;

    private static void EnsureClock() {
        if (Interlocked.CompareExchange(ref clockSubscribed, 1, 0) != 0)
            return;
        Service.Framework.Update += OnFrameworkUpdate;
    }

    private static bool FindBlocking(Dictionary<string, PressRecord> presses, nint address, string pressKey, bool singleAnswer, long frame, out string blockingKey) {
        foreach (var (key, rec) in presses) {
            if (rec.Address != address)
                continue;

            var waited = frame - rec.Frame;
            if (waited >= rec.EscapeFrames)
                continue;   // 冷了:交給同 key 的逃生口去判要不要補按

            var sameKey = string.Equals(key, pressKey, StringComparison.Ordinal);
            var blocks = sameKey
                         || singleAnswer
                         // 「我們要求它關閉」與別的按法互擋,但同一幀內不互擋
                         // (「按完順手關掉」是正常流程,關閉的效果還沒發生)。
                         || ((key == ClosePressKey || pressKey == ClosePressKey) && rec.Frame != frame);
            if (!blocks)
                continue;

            blockingKey = key;
            return true;
        }

        blockingKey = string.Empty;
        return false;
    }

    private static void LogHold(string addonName, nint address, string pressKey, string blockingKey, bool routine) {
        if (!LogThrottle($"Hold-{addonName}", 60))
            return;

        var msg = $"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                  $"按過之後(紀錄「{blockingKey}」)還沒觀察到它收掉,這一幀不再碰它 —— " +
                  "對關閉中的視窗送 callback 是攔不到的存取違規。";
        if (routine)
            Service.Log.Debug(msg);
        else
            Service.Log.Information(msg);
    }

    /// <summary>用守衛自己的幀時鐘做的節流,免得每幀路徑上的診斷洗版。</summary>
    private static bool LogThrottle(string key, int frames) {
        if (LogNextFrame.TryGetValue(key, out var next) && frameCount < next)
            return false;
        LogNextFrame[key] = frameCount + frames;
        return true;
    }

    /// <summary>
    /// 生命週期解除軌。
    /// <para>🔴 <b>解除要按位址,不能按名稱整包清。</b> 「同名的第二扇被建立 ⇒ 把整個名稱條目清掉」
    /// 會造出這條失效路徑:幀 F 對 #A 按下並登記;幀 F+1 #A 進入關閉幀(三關仍全過),
    /// 此時同名的 #B 被建立 → PostSetup → 整包被清;幀 F+2 按下點仍解到 #A、查無紀錄 → 放行
    /// ⇒ 對關閉中的 #A 送第二發 ⇒ 原生 AVE。</para>
    /// <para>🔴🔴 <b><see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄。</b>
    /// AddonLifecycle 監聽器<b>彼此之間</b>的呼叫順序不可依賴(服務端註冊走 <c>RunOnTick</c>,
    /// 派送時直接列舉不做快照),而 <c>PurificationManager.ResultsSetup</c> 就是
    /// 「在 PostSetup 處理常式裡直接按下去」的模組 —— 若守衛的 handler 排在它後面,
    /// 剛登記的紀錄會被自己的 PostSetup 清掉,守衛等於不存在。加上同幀豁免就把順序這個變數整個拿掉。</para>
    /// <para>📌 從生命週期處理常式裡呼叫 <c>RegisterListener</c> 是安全的:服務端把實際的
    /// 清單異動延到 <c>RunOnTick</c>,不會在派送當中改到正在被列舉的集合。</para>
    /// </summary>
    private static void EnsureWatching(string addonName) {
        if (Watchers.ContainsKey(addonName))
            return;

        IAddonLifecycle.AddonEventDelegate handler = (type, args) => {
            var address = args.Addon.Address;
            if (address == 0 || !PressedByAddon.TryGetValue(addonName, out var presses))
                return;

            LifecycleKeysBuf.Clear();
            foreach (var (key, rec) in presses) {
                if (rec.Address != address)
                    continue;
                if (type == AddonEvent.PostSetup && rec.Frame == frameCount)
                    continue;   // 這一幀才登記的 ⇒ 是「模組剛在這次 PostSetup 派送裡按下」,不是新的一扇
                LifecycleKeysBuf.Add(key);
            }

            foreach (var key in LifecycleKeysBuf)
                presses.Remove(key);

            if (presses.Count == 0)
                PressedByAddon.Remove(addonName);
        };

        Watchers[addonName] = handler;
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }
}
