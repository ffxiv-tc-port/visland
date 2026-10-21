using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using visland.Gathering.AutoGather;
using visland.Helpers;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace visland.Gathering;

public class GatherRouteExec : IDisposable {
    public GatherRouteDB RouteDB;
    public GatherRouteDB.Route? CurrentRoute;
    public int CurrentWaypoint;
    public bool ContinueToNext;
    public bool Paused;
    public bool Loop;
    public bool Waiting;
    public long WaitUntil;
    public bool Pathfind;
    public GatheringAddon.Gathering? GatheringAM;
    public GatheringAddon.Gathering.GatheredItem? GatheredItem;
    public GatheringAddon.GatheringMasterpiece? GatheringCollectableAM;

    public State CurrentState;
    public enum State {
        None,
        WaitingForNavmesh,
        Gathering,
        Paused,
        Pathfinding,
        Moving,
        WaitingForDestination,
        Eating,
        Mounting,
        Dismounting,
        Jumping,
        JobSwapping,
        Teleporting,
        Interacting,
        ExtractingMateria,
        PurifyingCollectables,
        RepairingGear,
        Waiting,
        WaitingForAutoRetainer,
        AdjustingPosition,
        YieldingToManualMove,
    }

    private readonly OverrideCamera _camera = new();
    private readonly OverrideMovement _movement = new();

    private readonly Throttle _interact = new();
    private readonly Queue<long> _recentErrors = new();
    private const int MaxRecentErrors = 5;

    // The repair chain runs on a task manager that clears its entire queue when a step exceeds 20s, and
    // CanRepairAny() is recomputed from live durability rather than latched. A repair that can never
    // succeed (repair action unusable, dark matter consumed mid-route, window never opens) would
    // therefore be re-queued every 20s forever and the route would silently stall in place.
    private int _repairAttempts;
    private const int MaxRepairAttempts = 3;

    // 尋路模式下使用者手動移動讓 vnavmesh 取消路徑時，不在下一幀就重送（照 BOCCHI 的讓位形狀）。
    private readonly ManualMoveYield _manualMove = new();
    private GatherRouteDB.Waypoint? _manualMoveWaypoint;

    private readonly RouteStuckWatch _stuck = new();
    private GatherRouteDB.Waypoint? _stuckWaypoint;

    /// <summary>往目前航點這一段的移動狀態；換航點、開始、停止都整份重設。只在 framework 執行緒讀寫。</summary>
    private sealed class Leg {
        public GatherRouteDB.Waypoint? Waypoint;
        public RouteLegPlan.Mode LastMode = RouteLegPlan.Mode.Straight;
        public Task<List<Vector3>>? Query;
        public CancellationTokenSource? QueryCts;
        public Vector3 QueryFrom;
        public bool QueryFly;
        /// <summary>坐騎飛行的航點還沒上坐騎時先算一條地面路徑，只用來判斷要不要上坐騎。</summary>
        public bool QueryIsProbe;
        public bool NavSent;
        public Vector3 PathEnd;
        public bool Straight;
        /// <summary>true＝這一段不上坐騎直接走；null＝還沒判。</summary>
        public bool? Walk;
    }

    private Leg _leg = new();
    // 交給 vnavmesh 的路徑可能還在走：跨航點保留（換段會重設 _leg），只在確定停掉或看到 vnavmesh 已閒置時清。
    private bool _ownNavPath;
    private string? _fallbackLogReason;
    private long _fallbackLogAt;
    private const long FallbackLogIntervalMs = 60_000;

    public GatherRouteExec() {
        RouteDB = Service.Config.Get<GatherRouteDB>();
        // These subscriptions drive "stop route on error": without them both CheckToDisable overloads are
        // dead code and the stop-on-error setting never triggers.
        Service.ChatGui.CheckMessageHandled += CheckToDisable;
        Service.Toasts.ErrorToast += CheckToDisable;
        Service.Framework.Update += Update;
    }

    public void Dispose() {
        Service.ChatGui.CheckMessageHandled -= CheckToDisable;
        Service.Toasts.ErrorToast -= CheckToDisable;
        Service.Framework.Update -= Update;
        Finish();
        _camera.Dispose();
        _movement.Dispose();
    }

    public void Start(GatherRouteDB.Route route, int waypoint, bool continueToNext, bool loopAtEnd, bool pathfind = false) {
        CurrentState = State.None;
        CurrentRoute = route;
        route.Waypoints.RemoveAll(x => x.IsPhantom);
        CurrentWaypoint = waypoint;
        ContinueToNext = continueToNext;
        Loop = loopAtEnd;
        route.Waypoints[waypoint].Pathfind = pathfind;
        Pathfind = pathfind;
        _repairAttempts = 0;
        _manualMove.Reset();
        _manualMoveWaypoint = null;
        _stuck.Reset();
        _stuckWaypoint = null;
        ResetLeg(null);
        _camera.Enabled = true;
        _movement.Enabled = true;
    }

    public void Finish() {
        SetState(State.None);
        if (CurrentRoute == null) return;

        // 已排入的傳送、換職業、修理、精製、精選都屬於這條路線：停下就一起丟掉（只排在路線期間，見各 Enqueue 呼叫端）。
        Service.TaskManager.Clear();
        _manualMove.Reset();
        _manualMoveWaypoint = null;
        _stuck.Reset();
        _stuckWaypoint = null;
        ResetLeg(null);
        CurrentRoute.Waypoints.RemoveAll(x => x.IsPhantom);
        CurrentRoute = null;
        CurrentWaypoint = 0;
        ContinueToNext = false;
        Waiting = false;
        Paused = false;
        _camera.Enabled = false;
        _movement.Enabled = false;
        CompatModule.RestoreChanges();
        StopOwnNavPath();
    }

    public unsafe void Update(IFramework _) {
        _camera.SpeedH = _camera.SpeedV = default;
        // MountNoFly 是邊走邊上坐騎，所以上坐騎的那幾幀目標點要留在航點上；
        // 其餘情況維持「目標＝自己現在的位置」＝原地不動。
        var mountWhileMoving = CurrentRoute != null && CurrentWaypoint < CurrentRoute.Waypoints.Count ? CurrentRoute.Waypoints[CurrentWaypoint] : null;
        _movement.DesiredPosition = CurrentState == State.Mounting && mountWhileMoving?.Movement == GatherRouteDB.Movement.MountNoFly ? mountWhileMoving.Position : Player.Object?.Position ?? new();

        if (Paused && Service.Navmesh.IsRunning()) {
            Service.Navmesh.Stop();
            _ownNavPath = false;
        }

        if (Paused && RouteDB.AutoRetainerIntegration && Service.Retainers.Finished && Service.Retainers.GetPreferredCharacter() == Player.CID) {
            Service.AutoRetainer.SetMultiEnabled(false);
            Paused = false;
        }

        if (!Player.Available || Player.IsCasting || Player.Mounting || Player.IsJumping || Paused || CurrentRoute == null || Service.TaskManager.IsBusy || CurrentWaypoint >= CurrentRoute.Waypoints.Count)
            return;

        CompatModule.EnsureCompatibility(RouteDB);

        if (RouteDB.AutoGather && GatheringAM is { CurrentIntegrity: > 0 } && GatheredItem is { IsEnabled: true } && !Player.InGatheringAnimation) {
            SetState(State.Gathering);
            GatheringActions.UseNextBestAction(GatheringAM, GatheredItem);
            return;
        }

        if (RouteDB.AutoGather && GatheringCollectableAM is { CurrentIntegrity: > 0 } && !Player.InGatheringAnimation) {
            SetState(State.Gathering);
            GatheringActions.UseNextBestAction(GatheringCollectableAM);
            return;
        }

        if (AddonUtils.IsOccupied()) return; // must check after auto gathering

        UpdateActiveRoute();
    }

    // 從 Update 拆出：下面的 lambda 捕獲 wp，留在 Update 裡的話沒有路線的每一幀也會在入口配置閉包。
    // 只由 Update 在判過 Player.Available、CurrentRoute 非 null、航點索引在範圍內之後呼叫。
    private unsafe void UpdateActiveRoute() {
        System.Diagnostics.Debug.Assert(CurrentRoute != null);
        var wp = CurrentRoute.Waypoints[CurrentWaypoint];
        var toWaypoint = wp.Position - Player.Object!.Position;
        var needToGetCloser = toWaypoint.LengthSquared() > wp.Radius * wp.Radius;
        Pathfind = wp.Pathfind;
        if (!ReferenceEquals(_leg.Waypoint, wp))
            ResetLeg(wp);

        // check if phantom waypoints have invalid interact data (i.e. from being added outside the object table distance)
        if (wp.IsPhantom && wp.InteractWithOID == 0) {
            var obj = Service.ObjectTable.FirstOrDefault(o => o?.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable && o?.Position.X - CurrentRoute.Waypoints[CurrentWaypoint].InteractWithPosition.X < 5 && o?.Position.Z - CurrentRoute.Waypoints[CurrentWaypoint].InteractWithPosition.Z < 5, null);
            if (obj != null) {
                wp.InteractWithOID = obj.BaseId;
                wp.InteractWithName = obj.Name.TextValue;
                wp.InteractWithPosition = obj.Position;
            }
        }

        var food = CurrentRoute.Food != 0 ? CurrentRoute.Food : RouteDB.GlobalFood != 0 ? RouteDB.GlobalFood : 0;
        if (food != 0 && Player.HasFood((uint)food) && !Player.HasFoodBuff && Player.AnimationLock == 0) {
            SetState(State.Eating);
            Service.Log.Debug($"Eating {Item.GetRow((uint)food)?.Name}");
            Player.EatFood(food);
            return;
        }

        if (RouteDB.AutoRetainerIntegration && (Service.Retainers.HasSubsReady || Service.Retainers.HasRetainersReady) && Service.Retainers.GetPreferredCharacter() == Player.CID) {
            SetState(State.WaitingForAutoRetainer);
            Paused = true;
            Service.Retainers.StartingCharacter = Player.CID;
            Service.AutoRetainer.SetMultiEnabled(true);
            return;
        }

        if (RouteDB.TeleportBetweenZones && wp.ZoneID != default && Coordinates.HasAetheryteInZone(wp.ZoneID) && Player.Territory != wp.ZoneID) {
            SetState(State.Teleporting);
            Service.Log.Information($"Teleporting from [{Player.Territory}] to [{wp.ZoneID}] {Coordinates.GetNearestAetheryte(wp.ZoneID, wp.Position)}");
            Service.TaskManager.Enqueue(() => Telepo.Instance()->Teleport(Coordinates.GetNearestAetheryte(wp.ZoneID, wp.Position), 0));
            Service.TaskManager.Enqueue(() => Player.IsCasting);
            Service.TaskManager.Enqueue(() => Player.Territory == wp.ZoneID);
            return;
        }

        if (wp.InteractWithOID != default && !Player.IsOnIsland && wp.IsNode && Player.Job != wp.NodeJob) {
            // must be done before movement or nodes will be skipped
            SetState(State.JobSwapping);
            Service.Log.Debug($"Changing job to {wp.NodeJob}");
            Service.TaskManager.Enqueue(() => Player.SwitchJob(wp.NodeJob));
            return;
        }

        if (needToGetCloser) {
            // skip current waypoint if target isn't there
            if (wp.IsNode && Player.DistanceTo(wp.Position) < 50 && !Service.ObjectTable.Any(x => x.BaseId == wp.InteractWithOID && x.IsTargetable)) {
                Service.Log.Debug("Current waypoint target is not targetable, moving to next waypoint");
                CancelLegQuery();
                StopOwnNavPath();
                goto next;
            }

            var mode = ChooseLegMode(wp);
            if (mode == RouteLegPlan.Mode.WaitForNavmesh) {
                SetState(State.WaitingForNavmesh);
                return;
            }
            var nav = mode == RouteLegPlan.Mode.Navmesh;

            if (nav && YieldToManualMovement(wp))
                return;

            if (HandleStuck(wp, toWaypoint.Length(), nav))
                return;

            if (Service.Navmesh.IsRunning()) {
                if (wp.Movement == GatherRouteDB.Movement.MountNoFly && !Player.Mounted && _leg.Walk != true) {
                    SetState(State.Mounting);
                    Player.Mount();
                    return;
                }

                SetState(State.WaitingForDestination);
                return;
            }

            if (nav)
                MoveWithNavmesh(wp);
            else
                MoveStraight(wp, toWaypoint);
            return;
        }

        // force stop at destination to avoid a bug wherein you interact with the object and keep moving for a period of time
        CancelLegQuery();
        StopOwnNavPath();

        if (!Player.Normal && wp.Movement == GatherRouteDB.Movement.Normal) {
            SetState(State.Dismounting);
            Player.Dismount();
            return;
        }

        if (Player.ExclusiveFlying && wp.Movement == GatherRouteDB.Movement.MountNoFly) {
            SetState(State.AdjustingPosition);
            _movement.DesiredPosition = new Vector3(Player.Position.X, wp.Position.Y, Player.Position.Z);
            Service.Log.Verbose($"Waypoint is MountNoFly, currently flying. Setting desired position lower.");
            return;
        }

        switch (wp.Interaction) {
            case GatherRouteDB.InteractionType.Standard:
                var interactObj = !AddonUtils.IsOccupied() ? FindObjectToInteractWith(wp) : null;
                if (interactObj != null) {
                    if (!Player.IsOnIsland && RouteDB.AutoGather && Player.Gp < 700) return;
                    _interact.Exec(() => {
                        SetState(State.Interacting);
                        Service.Log.Debug("Interacting...");
                        TargetSystem.Instance()->OpenObjectInteraction(interactObj);
                    });
                    return;
                }
                break;
            case GatherRouteDB.InteractionType.StartRoute:
                var route = RouteDB.Routes.Find(r => r.Name == wp.RouteName);
                if (route != null) {
                    Finish();
                    Start(route, 0, true, false, route.Waypoints[0].Pathfind);
                    return;
                }
                break;
            case GatherRouteDB.InteractionType.NodeScan:
                var objs = Service.ObjectTable.Where(o => o?.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable).OrderBy(x => x.BaseId);
                if (objs.Any()) {
                    Service.Log.Debug($"Found {objs.Count()} GatheringPoints");
                    TryAddObjects(wp, objs);
                }
                else {
                    var markers = GetGatheringMarkers();
                    if (markers.Count == 0) { Player.RevealNode(); return; }
                    TryAddMarkers(wp, markers);
                }
                if (!wp.IsLast(CurrentRoute)) {
                    ++CurrentWaypoint;
                    return;
                }
                break;
        }

        if (Service.TaskManager.IsBusy) return; // let interactions play out

        if (RouteDB.ExtractMateria && SpiritbondManager.IsSpiritbondReadyAny() && !AddonUtils.IsOccupied() && !Service.Condition[ConditionFlag.Mounted]) {
            SetState(State.ExtractingMateria);
            Service.Log.Debug("Extract materia task queued.");
            Service.TaskManager.Enqueue(SpiritbondManager.ExtractMateriaTask, "ExtractMateria");
            return;
        }

        var needsRepair = RouteDB.RepairGear && RepairAssistantManager.CanRepairAny(RouteDB.RepairPercent);
        if (!needsRepair) {
            _repairAttempts = 0; // gear is fine (or the feature is off) — a later repair starts from a clean slate
        }
        else if (!AddonUtils.IsOccupied() && !Service.Condition[ConditionFlag.Mounted]) {
            if (_repairAttempts < MaxRepairAttempts) {
                ++_repairAttempts;
                SetState(State.RepairingGear);
                Service.Log.Debug($"Repair gear task queued (attempt {_repairAttempts}/{MaxRepairAttempts}).");
                Service.TaskManager.Enqueue(RepairAssistantManager.ProcessRepair, "RepairGear");
                return;
            }
            if (_repairAttempts == MaxRepairAttempts) {
                ++_repairAttempts; // only complain once
                Service.Log.Error($"Gear still needs repair after {MaxRepairAttempts} attempts; continuing the route without repairing.");
            }
        }

        if (RouteDB.PurifyCollectables && PurificationManager.CanPurifyAny() && !AddonUtils.IsOccupied() && !Service.Condition[ConditionFlag.Mounted]) {
            SetState(State.PurifyingCollectables);
            Service.Log.Debug("Purify collectables task queued.");
            Service.TaskManager.Enqueue(PurificationManager.PurifyAllTask, "PurifyCollectables");
            return;
        }

    next:
        if (!ContinueToNext) {
            Finish();
            return;
        }

        _recentErrors.Clear(); // Resets errors between points in case gathering is still valid but just unable to gather all items from a node (e.g maxed out on stone, but not quartz)

        if (wp.WaitTimeET != default && wp.WaitTimeET != (Utils.EorzeanHour(), Utils.EorzeanMinute()).ToVec2()) return;

        if (!Waiting && wp.WaitTimeMs != default) {
            WaitUntil = Environment.TickCount64 + wp.WaitTimeMs;
            Waiting = true;
            SetState(State.Waiting);
        }

        if (Waiting && Environment.TickCount64 <= WaitUntil) return;

        if (wp.WaitForCondition != default && !Service.Condition[wp.WaitForCondition]) return;

        Waiting = false;

        if (wp.IsPhantom && wp.IsLast(CurrentRoute)) // phantom nodes should have two interactions: standard and nodescan. Ideally find a better way than just duplicating the function here
        {
            var objs = Service.ObjectTable.Where(o => o?.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable).OrderBy(x => x.BaseId);
            if (objs.Any()) {
                Service.Log.Debug($"Found {objs.Count()} GatheringPoints");
                TryAddObjects(wp, objs);
            }
            else {
                var markers = GetGatheringMarkers();
                if (markers.Count == 0) { Player.RevealNode(); return; }
                TryAddMarkers(wp, markers);
            }
            if (!wp.IsLast(CurrentRoute)) {
                // we don't want phantom waypoints to continue as normal or else it would reset the route
                ++CurrentWaypoint;
                return;
            }
        }

        if (++CurrentWaypoint >= CurrentRoute!.Waypoints.Count) {
            if (Loop) {
                CurrentRoute.Waypoints.RemoveAll(x => x.IsPhantom);
                CurrentWaypoint = 0;
            }
            else {
                // 🔴 這是整個檔案裡唯一「路線真的跑完了」的地方（走到最後一個點而且沒開循環）。
                //    本檔其餘 Finish() 呼叫點都不是：Dispose()＝卸載、StartRoute＝換路線、
                //    !ContinueToNext＝單點模式、StopOnErrors＝錯誤中止（走「需要幫忙」）。
                //    名字要在 Finish() 之前抄下來 —— Finish() 會把 CurrentRoute 設成 null。
                var routeName = CurrentRoute.Name;
                Finish();
                IPC.TataruPraiseIPC.TryPraiseRouteDone($"路線「{routeName}」跑完");
            }
        }
    }

    private void ResetLeg(GatherRouteDB.Waypoint? wp) {
        CancelLegQuery();
        _leg = new Leg { Waypoint = wp };
    }

    // 只停自己交給 vnavmesh 的路徑：Path.Stop 會停掉任何人送的路徑，不是自己的就不碰。
    // 自己的算路一律走 CancelLegQuery，不送會取消別的插件算路的 Nav.PathfindCancelAll。
    private void StopOwnNavPath() {
        if (_ownNavPath && Service.Navmesh.IsRunning())
            Service.Navmesh.Stop();
        _ownNavPath = false;
    }

    // 只取消自己這一筆；被取消的工作不讀結果（取消在那裡會擲例外），丟著讓它自己收場。
    private void CancelLegQuery() {
        if (_leg.Query == null)
            return;
        if (!_leg.Query.IsCompleted)
            _leg.QueryCts?.Cancel();
        else
            _ = _leg.Query.Exception;
        _leg.Query = null;
        _leg.QueryCts = null;
    }

    private RouteLegPlan.Mode ChooseLegMode(GatherRouteDB.Waypoint wp) {
        var wanted = wp.Pathfind || RouteDB.PathfindAllWaypoints;
        var installed = wanted && Service.Navmesh.IsEnabled;
        var ready = installed && !_leg.Straight && Service.Navmesh.IsReady();
        var mode = RouteLegPlan.Choose(wp.Pathfind, RouteDB.PathfindAllWaypoints, installed, ready, _leg.Straight);

        if (mode == RouteLegPlan.Mode.Straight && wanted && !_leg.Straight)
            LogFallback(installed ? "vnavmesh 的導航網格還沒建好（建好之後這一段會改用尋路）" : "沒有載入 vnavmesh");
        // 直線移動中角色本來就在跑：換成尋路時不能拿那段速度當成「使用者在手動移動」。
        if (mode == RouteLegPlan.Mode.Navmesh && _leg.LastMode != RouteLegPlan.Mode.Navmesh)
            _manualMove.Reset();
        _leg.LastMode = mode;
        return mode;
    }

    private void LogFallback(string reason) {
        var now = Environment.TickCount64;
        if (reason == _fallbackLogReason && now - _fallbackLogAt < FallbackLogIntervalMs)
            return;
        _fallbackLogReason = reason;
        _fallbackLogAt = now;
        Service.Log.Information($"[路線尋路] {reason}：航點 #{CurrentWaypoint} 先用直線移動。");
    }

    private void LatchStraight(GatherRouteDB.Waypoint wp, string reason) {
        CancelLegQuery();
        _leg.Straight = true;
        _leg.NavSent = false;
        Service.Log.Information($"[路線尋路] 航點 #{CurrentWaypoint}（{wp.Position:F1}）：{reason}，這一段剩下的改用直線移動。目前位置 {Player.Position:F1}");
    }

    private void DecideWalk(GatherRouteDB.Waypoint wp, float length, bool reachable, string source) {
        if (_leg.Walk != null || Player.Mounted || !wp.NeedsMount)
            return;
        _leg.Walk = reachable && RouteLegPlan.SkipMount(length, RouteDB.NoMountUnderDistance);
        if (_leg.Walk == true)
            Service.Log.Information($"[路線尋路] 航點 #{CurrentWaypoint}：這一段 {length:F1} 米（{source}），短於 {RouteDB.NoMountUnderDistance:F0} 米，不上坐騎直接走。");
    }

    /// <summary>直線移動（原本沒有尋路時的走法）。只在 framework 執行緒、路線的移動分支呼叫。</summary>
    private void MoveStraight(GatherRouteDB.Waypoint wp, Vector3 toWaypoint) {
        // 坐騎飛行的航點可能在半空中，直線距離短不代表走得到：直線移動只對地面坐騎套「短距離不上坐騎」。
        if (wp.Movement == GatherRouteDB.Movement.MountNoFly)
            DecideWalk(wp, toWaypoint.Length(), true, "直線距離");

        if (wp.Movement == GatherRouteDB.Movement.MountFly && !Player.Mounted && _leg.Walk != true) {
            SetState(State.Mounting);
            Player.Mount();
            return;
        }

        // MountNoFly 的上坐騎在下面（移動指令送出之後），這裡不送衝刺以免打斷騎乘。
        if (wp.Movement != GatherRouteDB.Movement.MountNoFly || _leg.Walk == true)
            Player.Sprint();

        if (wp.Movement == GatherRouteDB.Movement.MountFly && Player.Mounted && !Player.InclusiveFlying) {
            // TODO: improve, jump is not the best really...
            SetState(State.Jumping);
            Player.Jump();
            return;
        }

        // 同一幀只決定一次狀態：邊走邊上坐騎時直接設 Mounting（先設 Moving 再改會讓 SetState 去重失效、每幀多寫兩行 log）。
        var mountNow = wp.Movement == GatherRouteDB.Movement.MountNoFly && !Player.Mounted && _leg.Walk != true;
        SetState(mountNow ? State.Mounting : State.Moving);
        _movement.DesiredPosition = wp.Position;
        _camera.SpeedH = _camera.SpeedV = 360.Degrees();
        _camera.DesiredAzimuth = Angle.FromDirection(toWaypoint.X, toWaypoint.Z) + 180.Degrees();
        if (mountNow)
            Player.Mount();
    }

    /// <summary>vnavmesh 尋路移動：自己算路（拿得到路徑長度判斷要不要上坐騎）再交給 Path.MoveTo。只在 framework 執行緒、vnavmesh 沒在走路徑時呼叫。</summary>
    private void MoveWithNavmesh(GatherRouteDB.Waypoint wp) {
        // 呼叫端已判過 vnavmesh 沒在走路徑：之前交出去的那條已經走完或被停了，之後出現的路徑不是自己的。
        _ownNavPath = false;
        if (_leg.Query != null) {
            if (!_leg.Query.IsCompleted) {
                SetState(State.Pathfinding);
                return;
            }
            ConsumeLegQuery(wp);
            return;
        }

        if (_leg.NavSent) {
            if (RouteLegPlan.AfterPathStopped(Player.Position, _leg.PathEnd) == RouteLegPlan.AfterStop.Straight) {
                LatchStraight(wp, $"尋路路徑走完了，終點離航點還有 {Vector3.Distance(_leg.PathEnd, wp.Position):F1} 米（半徑 {wp.Radius:F1}）");
                return;
            }
            _leg.NavSent = false;
        }

        if (wp.Movement == GatherRouteDB.Movement.MountFly && !Player.Mounted && _leg.Walk != true) {
            if (_leg.Walk == null && RouteDB.NoMountUnderDistance > 0 && !Player.InclusiveFlying) {
                StartLegQuery(wp, false, true);
                return;
            }
            SetState(State.Mounting);
            Player.Mount();
            return;
        }

        if (wp.Movement == GatherRouteDB.Movement.MountFly && Player.Mounted && !Player.InclusiveFlying) {
            SetState(State.Jumping);
            Player.Jump();
            return;
        }

        StartLegQuery(wp, Player.InclusiveFlying || wp.Movement == GatherRouteDB.Movement.MountFly && Player.Mounted, false);
    }

    private void StartLegQuery(GatherRouteDB.Waypoint wp, bool fly, bool probe) {
        var from = Player.Position;
        var cts = new CancellationTokenSource();
        Task<List<Vector3>>? query;
        string? error = null;
        try {
            query = Service.Navmesh.PathfindCancelable(from, wp.Position, fly, cts.Token);
        }
        catch (Exception ex) {
            query = null;
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (query == null) {
            if (probe)
                _leg.Walk = false;
            else
                LatchStraight(wp, $"呼叫不到 vnavmesh 的尋路介面（{error ?? "端點不存在"}）");
            return;
        }

        if (!probe)
            _stuck.NoteSend();
        _leg.Query = query;
        _leg.QueryCts = cts;
        _leg.QueryFrom = from;
        _leg.QueryFly = fly;
        _leg.QueryIsProbe = probe;
        SetState(State.Pathfinding);
    }

    private void ConsumeLegQuery(GatherRouteDB.Waypoint wp) {
        var query = _leg.Query!;
        var probe = _leg.QueryIsProbe;
        var fly = _leg.QueryFly;
        var from = _leg.QueryFrom;
        _leg.Query = null;
        _leg.QueryCts = null;

        // 被取消（換網格、Nav.PathfindCancelAll）是正常收場：下一幀重算。
        if (query.IsCanceled)
            return;

        List<Vector3>? path = null;
        string? failure = null;
        if (query.IsFaulted)
            failure = query.Exception?.InnerException?.Message ?? "未知錯誤";
        else if ((path = query.Result) is not { Count: > 0 })
            failure = "路徑是空的";

        if (failure != null) {
            if (probe)
                _leg.Walk = false;
            else
                LatchStraight(wp, $"尋路失敗（{failure}）");
            return;
        }

        var player = Player.Position;
        if (RouteLegPlan.ResultStale(from, player))
            return;

        var length = RouteLegPlan.LegLength(player, path, wp.Position);
        DecideWalk(wp, length, wp.Movement != GatherRouteDB.Movement.MountFly || RouteLegPlan.PathReaches(path, wp.Position, wp.Radius), "尋路路徑");
        // 探路的結果是要上坐騎飛：丟掉地面路徑，下一幀走上坐騎、起飛、重新算飛行路徑。
        if (probe && _leg.Walk != true)
            return;

        if (!Player.Mounted && (wp.Movement == GatherRouteDB.Movement.Normal || _leg.Walk == true))
            Player.Sprint();
        var mountNow = wp.Movement == GatherRouteDB.Movement.MountNoFly && !Player.Mounted && _leg.Walk != true;
        SetState(mountNow ? State.Mounting : State.Moving);
        Service.Navmesh.MoveTo(path!, fly);
        _ownNavPath = true;
        _leg.NavSent = true;
        _leg.PathEnd = path![^1];
        if (mountNow)
            Player.Mount();
    }

    /// <summary>回 true＝使用者在手動移動（或正在確認是不是），這一幀不重送路徑。只在 framework 執行緒、尋路模式的移動分支呼叫。</summary>
    private bool YieldToManualMovement(GatherRouteDB.Waypoint wp) {
        if (!ReferenceEquals(wp, _manualMoveWaypoint)) {
            _manualMove.Reset();
            _manualMoveWaypoint = wp;
        }

        var navigating = _leg.Query != null || Service.Navmesh.IsRunning() || Service.Navmesh.PathfindInProgress();
        var busy = Player.Mounting || Player.IsJumping || Player.IsCasting
            || Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51];
        var player = Player.Position;
        switch (_manualMove.Observe(player, navigating, busy, Environment.TickCount64)) {
            case ManualMoveYield.Step.Started:
                Service.Log.Information($"[路線讓位] 導航停了、角色以 {_manualMove.LastSpeed:F1} 米/秒持續移動 {ManualMoveYield.ConfirmMs / 1000f:F1} 秒：判定你在手動移動，暫停重送路徑；"
                    + $"角色靜止 {ManualMoveYield.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續，單次讓位上限 {ManualMoveYield.MaxYieldMs / 1000} 秒。航點 {wp.Position:F1}");
                SetState(State.YieldingToManualMove);
                return true;
            case ManualMoveYield.Step.Hold:
                return true;
            case ManualMoveYield.Step.Resumed:
                Service.Log.Information($"[路線讓位] 角色已靜止，從目前位置 {player:F1} 接續前往航點 {wp.Position:F1}。");
                return false;
            case ManualMoveYield.Step.Expired:
                Service.Log.Information($"[路線讓位] 讓位超過 {ManualMoveYield.MaxYieldMs / 1000} 秒：前往這個航點的這一趟之後不再讓位，照原本的重送規則。");
                return false;
            default:
                return false;
        }
    }

    /// <summary>回 true＝這一幀已經處理卡住（重找路、跳躍或停下路線）。只在 framework 執行緒、路線的移動分支呼叫。</summary>
    private bool HandleStuck(GatherRouteDB.Waypoint wp, float distanceToWaypoint, bool pathfindMode) {
        if (!ReferenceEquals(wp, _stuckWaypoint)) {
            _stuck.Reset();
            _stuckWaypoint = wp;
        }

        // 只在自己的移動應該推著角色走時計時：算路中、還要先上坐騎的幀不算。
        var needsMountFirst = wp.Movement == GatherRouteDB.Movement.MountFly && !Player.Mounted && _leg.Walk != true;
        var counting = pathfindMode ? Service.Navmesh.IsRunning() : !needsMountFirst;
        var action = _stuck.Observe(Player.Position, distanceToWaypoint, counting, Environment.TickCount64);
        if (action == RouteStuckWatch.Action.None)
            return false;

        var where = $"航點 #{CurrentWaypoint}（{wp.Position:F1}），目前位置 {Player.Position:F1}、距離 {distanceToWaypoint:F1} 米";
        switch (action) {
            case RouteStuckWatch.Action.Repath when pathfindMode:
                Service.Log.Information($"[路線卡住] {where}：{RouteStuckWatch.NoProgressMs / 1000} 秒內沒有前進 {RouteStuckWatch.ProgressDistance} 米（或重送 {RouteStuckWatch.MaxSendsWithoutProgress} 次沒有進展），停下重新找路。");
                CancelLegQuery();
                _leg.NavSent = false;
                StopOwnNavPath();
                return true;
            case RouteStuckWatch.Action.Repath:
            case RouteStuckWatch.Action.Jump:
                Service.Log.Information($"[路線卡住] {where}：仍然沒有進展（第 {_stuck.Level} 次），跳一下。");
                Player.Jump();
                return true;
            default:
                Service.Log.Information($"[路線卡住] {where}：重新找路與跳躍後仍然沒有進展，停止路線。");
                Service.ChatGui.PrintError($"[{Service.Interface.InternalName}] 採集路線在航點 #{CurrentWaypoint} 附近卡住（重新找路與跳躍都沒有前進），已停止路線。");
                StopOnErrors("移動卡住");
                return true;
        }
    }

    private bool NodeExists(Vector3 nodePos) {
        if (Vector3.DistanceSquared(Player.Position, nodePos) < 100)
            if (Service.ObjectTable.FirstOrDefault(x => x?.Position == nodePos, null) != null)
                return true;
        return false;
    }

    private void SetState(State state) {
        if (state != CurrentState) {
            CurrentState = state;
            Service.Log.Information($"State: {CurrentState}");
        }
    }

    #region Interactions
    private unsafe GameObject* FindObjectToInteractWith(GatherRouteDB.Waypoint wp) {
        if (wp.InteractWithOID == 0)
            return null;

        foreach (var obj in Service.ObjectTable.Where(o => o.BaseId == wp.InteractWithOID && (o.Position - wp.Position).LengthSquared() < 1))
            return obj.IsTargetable ? (GameObject*)obj.Address : null;
        return null;
    }

    private void TryAddObjects(GatherRouteDB.Waypoint wp, IEnumerable<IGameObject> nodes) {
        List<GatherRouteDB.Waypoint>? waypoints = [];
        waypoints = [.. nodes.Select(obj => new GatherRouteDB.Waypoint {
            IsPhantom = true,
            Position = Service.Condition[ConditionFlag.Diving] ? obj.Position : Service.Navmesh.QueryMeshPointOnFloor(obj.Position, false, 5) ?? obj.Position,
            ZoneID = Service.ClientState.TerritoryType,
            Radius = RouteDB.DefaultWaypointRadius,
            InteractWithName = obj.Name.TextValue,
            InteractWithOID = obj.BaseId,
            InteractWithPosition = obj.Position,
            Interaction = GatherRouteDB.InteractionType.Standard,
            Movement = Player.InclusiveFlying ? GatherRouteDB.Movement.MountFly : GatherRouteDB.Movement.Normal
        })];

        if (waypoints.Count > 0)
            wp.AddWaypointsAfter(CurrentRoute!, waypoints);
    }

    private void TryAddMarkers(GatherRouteDB.Waypoint wp, List<(MiniMapGatheringMarker Marker, Vector3 Position, float DistanceToLast, IGameObject? Node)> markers) {
        List<GatherRouteDB.Waypoint>? waypoints = [];
        waypoints = [.. markers.Select(marker => new GatherRouteDB.Waypoint {
            IsPhantom = true,
            Position = Service.Condition[ConditionFlag.Diving] ? marker.Position : Service.Navmesh.QueryMeshPointOnFloor(marker.Position, false, 5) ?? marker.Position,
            ZoneID = Service.ClientState.TerritoryType,
            Radius = RouteDB.DefaultWaypointRadius,
            InteractWithName = marker.Node?.Name.TextValue ?? "",
            InteractWithOID = marker.Node?.BaseId ?? 0,
            InteractWithPosition = marker.Node?.Position ?? marker.Position,
            Interaction = GatherRouteDB.InteractionType.Standard,
            Movement = Service.Condition[ConditionFlag.Diving] || marker.DistanceToLast > 30 ? GatherRouteDB.Movement.MountFly : GatherRouteDB.Movement.Normal
        }).OrderBy(x => Vector3.Distance(Player.Position, x.Position))];

        if (waypoints.Count > 0)
            wp.AddWaypointsAfter(CurrentRoute!, waypoints);
    }

    // 🔴 AgentMap.Instance() 是產生器產出的取得子,本體即「agentModule == null ? null : ...」,
    //    換區/登入途中 AgentModule 還沒建好時合法回 null。裸解參考 = AccessViolationException,
    //    corrupted-state exception,try/catch 攔不到,沒有第二道防線。
    // fail-closed:拿不到地圖 agent 就回空清單。兩個呼叫端對空清單的既有反應都是
    //    「Player.RevealNode(); return;」—— 那是本來就會走到的路徑,退化行為良性。
    private unsafe List<(MiniMapGatheringMarker Marker, Vector3 Position, float DistanceToLast, IGameObject? Node)> GetGatheringMarkers() {
        var agent = AgentMap.Instance();
        if (agent == null)
            return [];
        return [.. agent->MiniMapGatheringMarkers.ToArray()
            .Where(x => x.MapMarker.IconId != 0)
            .Select((marker, index) => {
                var pos = new Vector3(marker.MapMarker.X / 16, Player.Position.Y, marker.MapMarker.Y / 16);
                var dist = index > 0 ? Vector3.Distance(Service.ObjectTable.ElementAt(index - 1).Position, pos) : 0;
                var obj = Service.ObjectTable.FirstOrDefault(o => o?.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable && o?.Position.X - CurrentRoute!.Waypoints[CurrentWaypoint].InteractWithPosition.X < 5 && o?.Position.Z - CurrentRoute.Waypoints[CurrentWaypoint].InteractWithPosition.Z < 5, null);
                Service.Log.Debug($"Found {nameof(MiniMapGatheringMarker)} @ {pos} {(obj != null ? $"and matching object [{obj.BaseId}] {obj.Name.TextValue} @ {obj.Position}" : string.Empty)}");
                return (Marker: marker, Position: obj != null ? obj.Position : pos, DistanceToLast: dist, Node: obj);
            })];
    }
    #endregion

    #region Error Checking
    private void CheckToDisable(ref SeString message, ref bool isHandled) {
        if (!RouteDB.DisableOnErrors) return;
        Service.Log.Verbose($"ErrorToast fired with string: {message}");
        RecordError();
        if (TooManyRecentErrors()) {
            Service.Log.Debug("Toast error threshold reached. Stopping route.");
            StopOnErrors("錯誤提示過多");
        }
    }

    private static readonly uint[] logErrors = [3570, 3574, 3575, 3584, 3589]; // various unable to spearfish errors

    /// <summary><see cref="logErrors"/> 那幾列拆好的比對形狀，第一次收到錯誤訊息時建起來。</summary>
    /// <remarks>🔴 部分列的開頭是 MacroCode.String（執行期才帶入角色名），Lumina <c>ExtractText()</c> 把巨集塌成空字串，「模板的文字」永遠不會等於實際收到的那一則訊息
    /// ⇒ 用 <c>==</c> 比對對這些列<b>恆為 false</b>，而且完全不報錯。
    /// 所以比對模板拆出來的靜態片段；沒有巨集的列，<see cref="SeStringTemplate.Shape.Matches"/> 仍然走完全相等。</remarks>
    private List<SeStringTemplate.Shape>? _logErrorShapes;

    private List<SeStringTemplate.Shape> LogErrorShapes() {
        if (_logErrorShapes != null) return _logErrorShapes;

        _logErrorShapes = [];
        foreach (var id in logErrors) {
            if (LogMessage.GetRow(id) is not { } row) {
                Service.Log.Information($"LogMessage#{id} 在本服的資料表裡不存在，這一列的採集錯誤訊息偵測停用。");
                continue;
            }

            if (SeStringTemplate.TryBuild(row.Text) is not { } shape) {
                Service.Log.Information($"LogMessage#{id} 的靜態文字不足 {SeStringTemplate.MinStaticChars} 個字元，不足以辨識，這一列的採集錯誤訊息偵測停用。");
                continue;
            }

            Service.Log.Information($"LogMessage#{id} 採集錯誤訊息比對形狀：{shape}");
            _logErrorShapes.Add(shape);
        }

        return _logErrorShapes;
    }

    private void CheckToDisable(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled) {
        if (!RouteDB.DisableOnErrors || type != XivChatType.ErrorMessage) return;

        Service.Log.Verbose($"ErrorMessage fired with string: {message}");
        // 兩端必須用同一套解析器：Dalamud 的 SeString 把連字符 payload(0x1F) 算成 U+2013「–」，
        // 而 B 端 Lumina 的 ExtractText() 算成 U+002D「-」⇒ 含連字符的訊息恆不相等。
        var msg = new ReadOnlySeStringSpan(message.Encode()).ExtractText();
        if (LogErrorShapes().FirstOrDefault(x => x.Matches(msg)) is { } hit) {
            // 🔑 要使用者回報的診斷：上面那行傾印是 Verbose、實機 log 收不到，
            //    不能拿它當「這條路徑沒走到」的依據。命中本身很罕見，不會洗版。
            Service.Log.Information($"採集錯誤訊息命中 {hit}：{msg}");
            RecordError();
        }
        if (TooManyRecentErrors()) {
            Service.Log.Debug("Chat error threshold reached. Stopping route.");
            StopOnErrors("聊天錯誤訊息過多");
        }
    }

    /// <summary>因為連續錯誤而中止路線，並（若使用者開著）請塔塔露念一句「需要幫忙」。</summary>
    /// <remarks>🔴 <b>要先判 <see cref="CurrentRoute"/> 不是 null 才算「這一次真的停掉了一條路線」。</b><see cref="_recentErrors"/> 只在 <see cref="Update"/> 走到下一個點時才清空，路線一停門檻會維持成立達 30 秒，
    /// 那段期間再來的每一則錯誤訊息都會再走一次這裡（<see cref="Finish"/> 本身是 no-op），沒有這道判斷會<b>連續念好幾次</b>。</remarks>
    /// <param name="reason">寫進記錄用的來源描述。</param>
    private void StopOnErrors(string reason) {
        var stopped = CurrentRoute != null;
        var routeName = CurrentRoute?.Name ?? string.Empty;
        Finish();
        if (stopped)
            IPC.TataruPraiseIPC.TryPraiseErrorStop($"路線「{routeName}」因{reason}被停下");
    }

    private void RecordError() {
        _recentErrors.Enqueue(Environment.TickCount64);
        while (_recentErrors.Count > MaxRecentErrors)
            _recentErrors.Dequeue();
    }

    private bool TooManyRecentErrors()
        => _recentErrors.Count >= MaxRecentErrors && _recentErrors.All(x => x > Environment.TickCount64 - 30_000);
    #endregion
}
