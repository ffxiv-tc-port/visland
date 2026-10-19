using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using visland.Export;
using visland.Farm;
using visland.Granary;
using visland.Helpers;
using visland.Pasture;
using visland.Workshop;
using static visland.Island.IslandTourLogic;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace visland.Island;

/// <summary>一鍵巡島：人已經在無人島上時，依序走到勾選的小員旁邊、說話、選第一項打開管理視窗，視窗裡的事交給 visland 既有的自動化。</summary>
/// <remarks>契約：
/// ①只有按下開始（按鈕或 <c>/visland tour</c>）才會動；載入、進區、登入都不會自己開始。
/// ②全部跑在 framework 執行緒（<see cref="Update"/>）；介面與 IPC 只設旗標或經閘門進來。
/// ③不跨幀保存任何原生指標或物件表包裝：每幀依種類重找小員、依名稱重取視窗。
/// ④任何一步找不到目標、走不到、等不到視窗就停下並寫一行 Information，不重試。</remarks>
public sealed unsafe class IslandTour : IDisposable {
    private enum Phase {
        Idle,
        Starting,
        Moving,
        Arrive,
        Dialog,
        Settling,
        HandOver,
        Closing,
        AfterClose,
    }

    private const long MoveGraceMs = 2000;
    private const long MoveTimeoutMs = 120_000;
    private const long ArriveTimeoutMs = 8000;
    private const long DialogTimeoutMs = 15_000;
    private const long SettleMs = 2500;
    private const long SettleWindowTimeoutMs = 8000;
    private const long CloseTimeoutMs = 5000;
    private const long AfterCloseTimeoutMs = 10_000;
    private const float FallbackInteractDistance = 4f;

    private readonly IslandTourConfig _config;
    private readonly IslandTourInterlocks _locks = new();
    private readonly Dictionary<uint, StopKind?> _kindByNpc = [];
    private readonly Throttle _dismount = new();

    private volatile bool _startRequested;
    private volatile bool _stopRequested;

    private Phase _phase;
    private List<StopKind> _plan = [];
    private int _index;
    private long _phaseSinceMs;
    private long _tourStartMs;
    private Dictionary<StopKind, LevelNpc> _levelPositions = [];
    private bool _picked;
    private bool _destIsLive;
    private Vector3 _dest;
    private long _windowOpenSinceMs;
    private int _reopens;

    public bool Running => _phase != Phase.Idle;
    public string Status { get; private set; } = string.Empty;
    public string LastResult { get; private set; } = string.Empty;

    public IslandTour() {
        _config = Service.Config.Get<IslandTourConfig>();
        Service.Framework.Update += Update;
    }

    public void Dispose() {
        Service.Framework.Update -= Update;
        if (Running)
            Stop("插件卸載");
    }

    /// <summary>可從任何執行緒呼叫：只設旗標，下一幀才真的開始。</summary>
    public void RequestStart() => _startRequested = true;

    /// <inheritdoc cref="RequestStart"/>
    public void RequestStop() => _stopRequested = true;

    public static string StopName(StopKind kind) => kind switch {
        StopKind.Workshop => "開拓工坊",
        StopKind.Granary => "屯貨倉庫",
        StopKind.Export => "產品交易",
        StopKind.Pasture => "牧場",
        StopKind.Farm => "耕地",
        _ => kind.ToString(),
    };

    public List<StopKind> CurrentPlan()
        => BuildPlan(_config.VisitWorkshop, _config.VisitGranary, _config.VisitExport, _config.VisitPasture, _config.VisitFarm);

    /// <summary>介面用來決定開始鈕能不能按；不能時 <paramref name="reason"/> 是給使用者看的一句話。</summary>
    public bool CanStart(out string reason) {
        reason = string.Empty;
        if (Running)
            reason = "巡島已經在跑。";
        else if (!Player.Available)
            reason = "角色目前不可操作。";
        else if (!Player.IsOnIsland)
            reason = "人要先在無人島上（上島的確認框請自己按）。";
        else if (!Service.Navmesh.IsEnabled)
            reason = "需要安裝並啟用 vnavmesh。";
        else if (!Service.Navmesh.IsReady())
            reason = "vnavmesh 的導航網格還沒載入完成。";
        else if (Service.RouteExec.CurrentRoute != null)
            reason = "採集路線正在跑。";
        else if (AddonUtils.IsOccupied())
            reason = "目前正在對話或操作中。";
        else if (CurrentPlan().Count == 0)
            reason = "一站都沒勾。";
        return reason.Length == 0;
    }

    /// <summary>只在 framework 執行緒呼叫（IPC 端點經 <c>IpcFrameworkGate</c>）。冪等。</summary>
    public void Stop(string reason) {
        if (!Running)
            return;
        if (_phase == Phase.Moving)
            Service.Navmesh.StopMovement();
        _locks.Release();
        _phase = Phase.Idle;
        Status = string.Empty;
        LastResult = $"巡島停止：{reason}";
        Service.Log.Information($"[巡島] 停止：{reason}");
    }

    private void Fail(string message) => Stop(message);

    // 本外掛所有 Framework.Update 處理器共用一個 try/catch：這裡擲例外會讓排在後面的處理器整幀被跳過，所以自己接住並停下。
    private void Update(IFramework _) {
        try {
            UpdateCore();
        }
        catch (Exception ex) {
            Service.Log.Error(ex, "[巡島] 執行中擲例外");
            Stop($"內部錯誤：{ex.GetType().Name}");
        }
    }

    private void UpdateCore() {
        if (_stopRequested) {
            _stopRequested = false;
            _startRequested = false;
            Stop("你按了停止");
        }
        if (_startRequested) {
            _startRequested = false;
            Begin();
        }
        if (!Running)
            return;

        _locks.Tick();
        if (!Player.Available) {
            Fail("角色目前不可操作（讀取中或倒地）。");
            return;
        }
        if (!Player.IsOnIsland) {
            Fail("已經不在無人島上。");
            return;
        }
        if (Service.RouteExec.CurrentRoute != null) {
            Fail("採集路線開始跑了，巡島讓位。");
            return;
        }

        switch (_phase) {
            case Phase.Moving: UpdateMoving(); break;
            case Phase.Arrive: UpdateArrive(); break;
            case Phase.Dialog: UpdateDialog(); break;
            case Phase.Settling: UpdateSettling(); break;
            case Phase.HandOver: UpdateHandOver(); break;
            case Phase.Closing: UpdateClosing(); break;
            case Phase.AfterClose: UpdateAfterClose(); break;
        }
    }

    private long Now => Environment.TickCount64;
    private long Elapsed => Now - _phaseSinceMs;
    private StopKind Kind => _plan[_index];
    private string Here => $"第 {_index + 1}/{_plan.Count} 站「{StopName(Kind)}」";

    private void Enter(Phase phase, string status) {
        _phase = phase;
        _phaseSinceMs = Now;
        Status = $"{Here}：{status}";
    }

    private void Begin() {
        if (!CanStart(out var why)) {
            LastResult = $"巡島沒有開始：{why}";
            Service.Log.Information($"[巡島] 沒有開始：{why}");
            return;
        }

        _plan = CurrentPlan();
        _levelPositions = BuildLevelPositions(out var ambiguous);
        if (ambiguous.Count > 0)
            Service.Log.Information($"[巡島] 表上同一站有兩個以上的小員，不採用表上位置：{string.Join("、", ambiguous.ConvertAll(StopName))}");

        if (!_locks.TryAcquire(out var failure)) {
            LastResult = $"巡島沒有開始：{failure}";
            Service.Log.Information($"[巡島] 沒有開始：{failure}");
            return;
        }

        _tourStartMs = Now;
        _index = 0;
        LastResult = string.Empty;
        Service.Log.Information($"[巡島] 開始：{string.Join("→", _plan.ConvertAll(StopName))}；表上有位置的站＝{_levelPositions.Count}；YesAlready 已暫停＝{_locks.YesAlreadyHeld}，TextAdvance 已暫停＝{_locks.TextAdvanceHeld}");
        _phase = Phase.Starting;
        StartStop();
    }

    private void StartStop() {
        if (_index >= _plan.Count) {
            Complete();
            return;
        }

        _picked = false;
        _windowOpenSinceMs = 0;
        _reopens = 0;
        var kind = Kind;
        var npc = FindNpc(kind);
        Vector3 dest;
        string source;
        if (npc != null) {
            dest = npc.Position;
            source = $"附近的{npc.Name.TextValue}";
        }
        else if (_levelPositions.TryGetValue(kind, out var lv)) {
            dest = new(lv.X, lv.Y, lv.Z);
            source = $"遊戲表上的位置（NPC #{lv.NpcId}）";
        }
        else {
            Fail($"{Here}：附近沒看到這一站的小員，遊戲表上也沒有他的位置。可能是那棟建築還沒蓋，或要先走近一點再按。");
            return;
        }

        Service.Log.Information($"[巡島] {Here}：目標＝{source}，距離 {Vector3.Distance(Player.Position, dest):f1}");
        if (npc != null && InRange(npc)) {
            Enter(Phase.Arrive, "已在旁邊");
            return;
        }
        _destIsLive = npc != null;
        MoveTo(dest);
    }

    private void MoveTo(Vector3 dest) {
        _dest = dest;
        var target = Service.Navmesh.QueryMeshNearestPoint(dest, 3, 5) ?? dest;
        if (!Service.Navmesh.PathfindAndMoveTo(target, false)) {
            Fail($"{Here}：vnavmesh 沒有接受移動請求。");
            return;
        }
        Player.Sprint();
        Enter(Phase.Moving, "移動中");
    }

    private void UpdateMoving() {
        var npc = FindNpc(Kind);
        var inRange = npc != null && InRange(npc);
        // 照表上位置走的途中看到本人了：改成直接走向他（只改一次，不是重試）。
        if (!inRange && npc != null && !_destIsLive) {
            _destIsLive = true;
            Service.Log.Information($"[巡島] {Here}：看到{npc.Name.TextValue}了，改走向他");
            MoveTo(npc.Position);
            return;
        }
        var busy = Service.Navmesh.IsRunning() || Service.Navmesh.PathfindInProgress();
        switch (JudgeMove(inRange, busy, Elapsed, MoveGraceMs, MoveTimeoutMs)) {
            case MoveVerdict.Arrived:
                if (busy)
                    Service.Navmesh.StopMovement();
                Enter(Phase.Arrive, "到了，準備說話");
                break;
            case MoveVerdict.Failed:
                if (busy)
                    Service.Navmesh.StopMovement();
                var far = Vector3.Distance(Player.Position, _dest) > 8;
                Fail(Elapsed > MoveTimeoutMs
                    ? $"{Here}：走了 {MoveTimeoutMs / 1000} 秒還沒到小員旁邊。"
                    : far
                        ? $"{Here}：還沒走到目的地移動就停了（可能是手動移動打斷，或 vnavmesh 找不到路）。"
                        : npc == null
                            ? $"{Here}：走到了但沒看到小員（建築可能還沒蓋）。"
                            : $"{Here}：走到了但還不夠近，沒辦法和小員說話。");
                break;
        }
    }

    private void UpdateArrive() {
        var npc = FindNpc(Kind);
        if (npc == null) {
            Fail($"{Here}：到了之後小員不見了。");
            return;
        }
        if (Elapsed > ArriveTimeoutMs) {
            Fail($"{Here}：到了 {ArriveTimeoutMs / 1000} 秒還沒辦法和小員說話（下不了坐騎或一直在忙）。");
            return;
        }
        if (Service.Navmesh.IsRunning()) {
            Service.Navmesh.Stop();
            return;
        }
        if (Player.Mounted) {
            _dismount.Exec(Player.Dismount, 1);
            return;
        }
        if (Player.IsCasting || Player.IsJumping || AddonUtils.IsOccupied())
            return;

        var ts = TargetSystem.MemberFunctionPointers.InteractWithObject != null ? TargetSystem.Instance() : null;
        if (ts == null) {
            Fail($"{Here}：取不到遊戲的目標系統，沒辦法說話。");
            return;
        }
        Service.TargetManager.Target = npc;
        ts->InteractWithObject((GameObject*)npc.Address, false);
        Service.Log.Information($"[巡島] {Here}：和{npc.Name.TextValue}說話");
        Enter(Phase.Dialog, $"和{npc.Name.TextValue}說話");
    }

    private void UpdateDialog() {
        var kind = Kind;
        var addonName = AddonName(kind);
        if (TryGetReady(addonName, out _)) {
            Enter(Phase.Settling, "視窗開了，等 visland 處理");
            return;
        }
        if (Elapsed > DialogTimeoutMs) {
            Fail($"{Here}：說話後 {DialogTimeoutMs / 1000} 秒內沒等到管理視窗。");
            return;
        }
        if (TryGetReady("SelectString", out var menu)) {
            if (_picked)
                return;
            if (!TargetLooksLike(kind)) {
                Fail($"{Here}：選單不是巡島找的那位小員開的，不替你選。");
                return;
            }
            var count = ((AddonSelectString*)menu)->PopupMenu.PopupMenu.EntryCount;
            if (count < 2) {
                Fail($"{Here}：小員的選單只有 {count} 項，和預期不同，不替你選。");
                return;
            }
            if (Pick(menu, 0)) {
                _picked = true;
                Service.Log.Information($"[巡島] {Here}：選了選單第 1 項（共 {count} 項）");
            }
            return;
        }
        if (_locks.MayClickTalk && TryGetReady("Talk", out var talk))
            ClickTalk(talk);
    }

    private void UpdateSettling() {
        var kind = Kind;
        if (!TryGetVisible(AddonName(kind), out _)) {
            Enter(Phase.AfterClose, "視窗已關，收尾");
            return;
        }

        if (IsVislandWindowOpen(kind)) {
            if (_windowOpenSinceMs == 0)
                _windowOpenSinceMs = Now;
        }
        else {
            _windowOpenSinceMs = 0;
        }

        if (_windowOpenSinceMs != 0 && Now - _windowOpenSinceMs >= SettleMs) {
            var automationOn = AutomationOn(kind);
            var known = TryNothingLeft(kind, out var nothingLeft);
            var action = DecideWindow(kind, automationOn, known && nothingLeft);
            var left = !known ? "不知道" : nothingLeft ? "沒有" : "有";
            Service.Log.Information($"[巡島] {Here}：視窗開了；visland 自動化＝{(automationOn ? "開" : "關")}，還有東西可收＝{left} → {(action == WindowAction.AutoClose ? "自動關閉" : "交給你")}");
            if (action == WindowAction.AutoClose)
                Enter(Phase.Closing, "關閉視窗");
            else
                EnterHandOver();
            return;
        }

        if (Elapsed > SettleWindowTimeoutMs) {
            Service.Log.Information($"[巡島] {Here}：{SettleWindowTimeoutMs / 1000} 秒內沒看到 visland 的自動化視窗（可能隱藏了插件介面），這一站交給你。");
            EnterHandOver();
        }
    }

    private void EnterHandOver() {
        Enter(Phase.HandOver, "交給你：處理完關掉這個視窗，巡島會接著走");
        Service.Log.Information($"[巡島] {Here}：視窗交給你，關掉之後接著走下一站。");
    }

    private void UpdateHandOver() {
        if (!TryGetVisible(AddonName(Kind), out _))
            Enter(Phase.AfterClose, "視窗已關，收尾");
    }

    private void UpdateClosing() {
        var addonName = AddonName(Kind);
        if (!TryGetVisible(addonName, out var addon)) {
            Enter(Phase.AfterClose, "視窗已關，收尾");
            return;
        }
        if (Elapsed > CloseTimeoutMs) {
            Fail($"{Here}：{CloseTimeoutMs / 1000} 秒內關不掉管理視窗。");
            return;
        }
        if (AddonPressGuard.TryBeginPress(addonName, (nint)addon, AddonPressGuard.ClosePressKey))
            addon->Close(true);
    }

    private void UpdateAfterClose() {
        if (Elapsed > AfterCloseTimeoutMs) {
            Fail($"{Here}：視窗關掉後 {AfterCloseTimeoutMs / 1000} 秒還在對話中。");
            return;
        }
        // 開拓工坊的選單在管理視窗關掉之後會再出現一次；最後一項是「取消」。
        if (TryGetReady("SelectString", out var menu)) {
            if (!TargetLooksLike(Kind)) {
                Fail($"{Here}：關掉視窗後出現的選單不是這位小員的，不替你選。");
                return;
            }
            var count = ((AddonSelectString*)menu)->PopupMenu.PopupMenu.EntryCount;
            if (count >= 1 && Pick(menu, count - 1))
                Service.Log.Information($"[巡島] {Here}：選單又出現了，選最後一項（取消）");
            return;
        }
        if (TryGetReady("Talk", out var talk)) {
            if (_locks.MayClickTalk)
                ClickTalk(talk);
            return;
        }
        // 產品交易賣完會自己關掉再開一次：重新等它處理；超過兩次就交給使用者，不跟它來回。
        if (TryGetReady(AddonName(Kind), out _)) {
            _windowOpenSinceMs = 0;
            if (++_reopens <= 2)
                Enter(Phase.Settling, "視窗又開了，等 visland 處理");
            else
                EnterHandOver();
            return;
        }
        if (AddonUtils.IsOccupied() || TryGetVisible(AddonName(Kind), out _))
            return;

        Service.Log.Information($"[巡島] {Here}：完成");
        ++_index;
        StartStop();
    }

    private void Complete() {
        var seconds = (Now - _tourStartMs) / 1000;
        _locks.Release();
        _phase = Phase.Idle;
        Status = string.Empty;
        LastResult = $"巡島完成：走了 {_plan.Count} 站，{seconds} 秒。";
        Service.Log.Information($"[巡島] 完成：走了 {_plan.Count} 站，用時 {seconds} 秒");
    }

    #region 找小員
    private StopKind? KindOf(uint npcId) {
        if (_kindByNpc.TryGetValue(npcId, out var cached))
            return cached;
        StopKind? kind = null;
        if (Service.DataManager.GetExcelSheet<ENpcBase>().GetRowOrDefault(npcId) is { } row) {
            var data = new List<uint>(row.ENpcData.Count);
            for (var i = 0; i < row.ENpcData.Count; ++i)
                data.Add(row.ENpcData[i].RowId);
            kind = ClassifyNpc(data, CustomTalkName);
        }
        _kindByNpc[npcId] = kind;
        return kind;
    }

    private static string? CustomTalkName(uint id)
        => Service.DataManager.GetExcelSheet<CustomTalk>().GetRowOrDefault(id) is { } row ? row.Name.ExtractText() : null;

    private Dictionary<StopKind, LevelNpc> BuildLevelPositions(out List<StopKind> ambiguous) {
        var territory = Player.Territory;
        List<LevelNpc> rows = [];
        foreach (var lv in Service.DataManager.GetExcelSheet<Level>())
            if (lv.Territory.RowId == territory && lv.Object.RowId != 0)
                rows.Add(new(lv.Object.RowId, lv.X, lv.Y, lv.Z));
        return PositionsFromLevel(rows, KindOf, out ambiguous);
    }

    /// <summary>這一幀物件表裡最近的、可選取的該站小員；回傳值只能在這一幀內用。</summary>
    private IGameObject? FindNpc(StopKind kind) {
        IGameObject? best = null;
        var bestDist = float.MaxValue;
        var me = Player.Position;
        foreach (var obj in Service.ObjectTable) {
            if (obj == null || obj.ObjectKind != ObjectKind.EventNpc || !obj.IsTargetable)
                continue;
            if (KindOf(obj.BaseId) != kind)
                continue;
            var d = Vector3.DistanceSquared(obj.Position, me);
            if (d < bestDist) {
                best = obj;
                bestDist = d;
            }
        }
        return best;
    }

    /// <summary>目前目標是不是這一站的小員；沒有目標時不擋（剛說過話，選單就是他開的）。</summary>
    private bool TargetLooksLike(StopKind kind) {
        var target = Service.TargetManager.Target;
        return target == null || KindOf(target.BaseId) == kind;
    }

    private static bool InRange(IGameObject npc) {
        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return false;
        if (EventFramework.MemberFunctionPointers.CheckInteractRange != null) {
            try {
                var ef = EventFramework.Instance();
                if (ef != null)
                    return ef->CheckInteractRange((GameObject*)player.Address, (GameObject*)npc.Address, 1, false);
            }
            catch (InvalidOperationException) {
                // 特徵碼沒對上：退回距離判斷。
            }
        }
        var flat = new Vector2(npc.Position.X - player.Position.X, npc.Position.Z - player.Position.Z).Length();
        return flat <= FallbackInteractDistance && MathF.Abs(npc.Position.Y - player.Position.Y) < 3;
    }
    #endregion

    #region 視窗
    private static bool TryGetReady(string name, out AtkUnitBase* addon)
        => AddonUtils.TryGetAddonByName(name, out addon) && AddonUtils.IsAddonReady(addon);

    private static bool TryGetVisible(string name, out AtkUnitBase* addon)
        => AddonUtils.TryGetAddonByName(name, out addon) && addon->IsVisible;

    private static bool Pick(AtkUnitBase* menu, int index) {
        if (!AddonPressGuard.TryBeginPress("SelectString", (nint)menu, "IslandTourPick"))
            return false;
        var value = stackalloc AtkValue[1];
        value[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        value[0].Int = index;
        menu->FireCallback(1, value, true);
        return true;
    }

    /// <summary>點一下對話框（與 TextAdvance／YesAlready 推進對話的事件序列相同）。</summary>
    private static void ClickTalk(AtkUnitBase* talk) {
        AtkStage* stage;
        try {
            stage = AtkStage.Instance();
        }
        catch (InvalidOperationException) {
            return;
        }
        if (stage == null || !AddonPressGuard.TryBeginPress("Talk", (nint)talk, "IslandTourTalk"))
            return;
        var evt = stackalloc AtkEvent[1];
        evt[0] = new AtkEvent {
            Listener = (AtkEventListener*)talk,
            Target = &stage->AtkEventTarget,
            State = new() { StateFlags = (AtkEventStateFlags)132 },
        };
        var data = stackalloc AtkEventData[1];
        new Span<byte>(data, sizeof(AtkEventData)).Clear();
        talk->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        talk->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
    }

    private static bool IsVislandWindowOpen(StopKind kind) => kind switch {
        StopKind.Workshop => Plugin.P.IsWindowOpen<WorkshopWindow>(),
        StopKind.Granary => Plugin.P.IsWindowOpen<GranaryWindow>(),
        StopKind.Export => Plugin.P.IsWindowOpen<ExportWindow>(),
        StopKind.Pasture => Plugin.P.IsWindowOpen<PastureWindow>(),
        StopKind.Farm => Plugin.P.IsWindowOpen<FarmWindow>(),
        _ => false,
    };

    /// <summary>visland 打開這扇視窗時會不會自己動手（與各視窗 <c>OnOpen</c> 的條件一致）。</summary>
    private static bool AutomationOn(StopKind kind) => kind switch {
        StopKind.Granary => Service.Config.Get<GranaryConfig>().Reassign != GranaryConfig.UpdateStrategy.Manual,
        StopKind.Export => Service.Config.Get<ExportConfig>().AutoSell,
        StopKind.Pasture => Service.Config.Get<PastureConfig>().Collect != CollectStrategy.Manual,
        StopKind.Farm => Service.Config.Get<FarmConfig>().Collect != CollectStrategy.Manual,
        _ => false,
    };

    /// <returns>讀不到狀態時回 false（＝不知道），呼叫端一律交給使用者。</returns>
    private static bool TryNothingLeft(StopKind kind, out bool nothingLeft) {
        nothingLeft = false;
        switch (kind) {
            case StopKind.Granary:
                if (GranaryUtils.State() == null)
                    return false;
                nothingLeft = GranaryUtils.CalculateGranaryCollectionState(0) == CollectResult.NothingToCollect
                              && GranaryUtils.CalculateGranaryCollectionState(1) == CollectResult.NothingToCollect;
                return true;
            case StopKind.Pasture: {
                var mji = MJIManager.Instance();
                if (mji == null || mji->PastureHandler == null)
                    return false;
                nothingLeft = PastureWindow.CalculateCollectResult() == CollectResult.NothingToCollect;
                return true;
            }
            case StopKind.Farm: {
                var mji = MJIManager.Instance();
                if (mji == null || mji->FarmState == null || AgentMJIFarmManagement.Instance() == null)
                    return false;
                nothingLeft = FarmWindow.CalculateCollectResult() == CollectResult.NothingToCollect;
                return true;
            }
            case StopKind.Export:
                nothingLeft = true;
                return true;
            default:
                return true;
        }
    }
    #endregion
}
