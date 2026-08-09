using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Text;
using visland.Gathering;
using visland.Helpers;

namespace visland.Island;

// 缺料總表的 UI。純顯示 —— 這一頁不會送出任何遊戲操作。
//
// 為什麼放在 /visland 主視窗而不是倉庫/工坊視窗:那兩個是 UIAttachedWindow,
// 只有站在對應建築前開了原生介面才看得見,而「還缺什麼」正是要在跑路線之前先看的東西。
public sealed class MaterialLedgerTab {
    private readonly MaterialLedger _ledger = new();
    private int _horizon = MaterialLedger.HorizonTwoWeeks;
    private bool _onlyShortages = true;
    private bool _hideLocked = true;
    private readonly List<MaterialLedgerRow> _visible = [];

    private List<RouteCoverage> _coverage = [];
    private readonly HashSet<uint> _shortages = [];
    private long _nextCoverageRefresh;
    private const int CoverageRefreshMs = 2000;

    private static readonly uint ColShortage = 0xff4f53d9; // ABGR:紅
    private static readonly uint ColUnknown = 0xff909090;  // 灰:代表「不知道」,不是 0

    public void Draw() {
        _ledger.Refresh();
        RefreshCoverage();
        DrawHeader();
        DrawControls();
        ImGui.Separator();
        DrawTable();
    }

    private void RefreshCoverage() {
        var now = Environment.TickCount64;
        if (now < _nextCoverageRefresh)
            return;
        _nextCoverageRefresh = now + CoverageRefreshMs;
        _coverage = RouteMatcher.Compute(Service.RouteExec.RouteDB.Routes);
    }

    private void DrawHeader() {
        if (!_ledger.IslandDataAvailable) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("Island data is not loaded - visit your Island Sanctuary once.".Loc());
            return;
        }
        if (!_ledger.DemandKnown) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("Workshop agenda data has not been read yet - demand is shown as ?.".Loc());
        }
        if (_ledger.StockFrozen) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("Pouch counts are stale (zoning, or island data not loaded yet).".Loc());
        }
        if (!_ledger.OnIsland) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("Incoming amounts (granary, farm, pasture) can only be read while you are on the island.".Loc());
        }
        else if (!_ledger.IncomingKnown) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("Some incoming sources could not be read, so gaps may be overstated.".Loc());
        }
    }

    private void DrawControls() {
        ImGui.TextV("Demand range:".Loc() + " ");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        string[] horizons = ["Current cycle".Loc(), "This week".Loc(), "This + next week".Loc()];
        UICombo.Int("##horizon", horizons, ref _horizon);
        ImGuiComponents.HelpMarker("Which of the three material allocation buckets the game keeps is used for the Need column. The labels come from client struct comments and have not been verified in game yet - the tooltip always shows all three.".Loc());

        ImGui.SameLine();
        ImGui.Checkbox("Only shortages".Loc(), ref _onlyShortages);
        ImGui.SameLine();
        ImGui.Checkbox("Hide undiscovered".Loc(), ref _hideLocked);
    }

    private void DrawTable() {
        _visible.Clear();
        foreach (var row in _ledger.Rows) {
            if (row.Info.ItemId == 0)
                continue;
            if (_hideLocked && !row.Unlocked)
                continue;
            if (_onlyShortages && _ledger.GapKnown(row) && _ledger.Gap(row, _horizon) <= 0)
                continue;
            _visible.Add(row);
        }
        _visible.Sort((a, b) => {
            var ga = _ledger.GapKnown(a) ? _ledger.Gap(a, _horizon) : int.MinValue;
            var gb = _ledger.GapKnown(b) ? _ledger.Gap(b, _horizon) : int.MinValue;
            return gb != ga ? gb.CompareTo(ga) : a.Info.PouchId.CompareTo(b.Info.PouchId);
        });

        _shortages.Clear();
        foreach (var row in _ledger.Rows)
            if (_ledger.GapKnown(row) && _ledger.Gap(row, _horizon) > 0)
                _shortages.Add(row.Info.PouchId);

        if (_visible.Count == 0) {
            ImGui.TextUnformatted("Nothing to show.".Loc());
            return;
        }

        using var table = ImRaii.Table("materials", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Material".Loc(), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Gap".Loc(), ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Have / Need".Loc(), ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Source".Loc(), ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Route".Loc(), ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableHeadersRow();

        foreach (var row in _visible)
            DrawRow(row);
    }

    private void DrawRow(MaterialLedgerRow row) {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown, !row.Unlocked))
            ImGui.TextUnformatted(row.Info.Name);
        if (ImGui.IsItemHovered())
            DrawTooltip(row);

        ImGui.TableNextColumn();
        if (!_ledger.GapKnown(row)) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("?");
        }
        else {
            var gap = _ledger.Gap(row, _horizon);
            using (ImRaii.PushColor(ImGuiCol.Text, ColShortage, gap > 0))
                ImGui.TextUnformatted(gap > 0 ? $"{gap}" : "-");
        }

        ImGui.TableNextColumn();
        var have = _ledger.StockKnown ? row.Stock.ToString() : "?";
        var need = _ledger.DemandKnown ? row.Demand[_horizon].ToString() : "?";
        var incoming = row.Incoming;
        ImGui.TextUnformatted(incoming > 0 ? $"{have} (+{incoming}) / {need}" : $"{have} / {need}");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(SourceBadges(row.Info));

        ImGui.TableNextColumn();
        DrawRouteCell(row);
    }

    // 找出覆蓋這個材料的路線,按「順手能一起補幾種你缺的材料」排序。
    private List<RouteCoverage> CandidateRoutes(uint pouchId) {
        List<RouteCoverage> candidates = [];
        foreach (var cov in _coverage)
            if (cov.HitsByPouch.ContainsKey(pouchId))
                candidates.Add(cov);
        candidates.Sort((a, b) => {
            var sa = ShortageCount(a);
            var sb = ShortageCount(b);
            if (sa != sb)
                return sb.CompareTo(sa);
            var ha = a.HitsByPouch[pouchId];
            var hb = b.HitsByPouch[pouchId];
            return hb != ha ? hb.CompareTo(ha) : a.MaterialCount.CompareTo(b.MaterialCount);
        });
        return candidates;
    }

    private int ShortageCount(RouteCoverage cov) {
        var n = 0;
        foreach (var pouchId in cov.HitsByPouch.Keys)
            if (_shortages.Contains(pouchId))
                ++n;
        return n;
    }

    private void DrawRouteCell(MaterialLedgerRow row) {
        if (row.Info.Gather == null)
            return; // 不是靠採集拿的材料,路線欄留白

        var candidates = CandidateRoutes(row.Info.PouchId);
        if (candidates.Count == 0) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("-");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("No saved route covers this material.".Loc());
            return;
        }

        var best = candidates[0];
        var running = Service.RouteExec.CurrentRoute != null;
        using (ImRaii.PushId((int)row.Info.PouchId)) {
            using (ImRaii.Disabled(running || best.Route.Waypoints.Count == 0)) {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Play))
                    // 沿用路線編輯器那顆播放鍵的手動語意:從第一點開始、走完就停、不迴圈。
                    Service.RouteExec.Start(best.Route, 0, true, false, best.Route.Waypoints[0].Pathfind);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(running ? "A route is already running.".Loc() : "Run ??".Loc(best.Route.Name));

            ImGui.SameLine();
            ImGui.TextUnformatted(Shorten(best.Route.Name, 14));
            if (ImGui.IsItemHovered())
                DrawRouteTooltip(row, candidates);
        }
    }

    private void DrawRouteTooltip(MaterialLedgerRow row, List<RouteCoverage> candidates) {
        var sb = new StringBuilder();
        sb.Append("Routes covering this material:".Loc()).Append('\n');
        var shown = 0;
        foreach (var cov in candidates) {
            if (shown++ >= 8) {
                sb.Append("  ...").Append('\n');
                break;
            }
            sb.Append("  ").Append(cov.Route.Name).Append('\n');
            sb.Append("    ").Append("covers ?? material(s), ?? of them short - ?? waypoint(s) here".Loc(
                cov.MaterialCount, ShortageCount(cov), cov.HitsByPouch[row.Info.PouchId])).Append('\n');
            if (cov.Approximate)
                sb.Append("    ").Append("No interaction waypoints in this route, so the match is approximate.".Loc()).Append('\n');
        }
        sb.Append('\n');
        sb.Append("Routes are matched by coordinates only, so a route can be listed for materials it does not actually gather.".Loc());
        ImGui.SetTooltip(sb.ToString());
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");

    private static string SourceBadges(MaterialInfo info) {
        List<string> badges = [];
        if (info.Gather != null)
            badges.Add("Gathering".Loc());
        if (info.RareExpeditionAreas.Count > 0 || info.ExpeditionAreas.Count > 0)
            badges.Add("Granary expedition".Loc()); // 刻意不用 "Expedition" —— 那個 key 已經是倉庫 UI 的「探索目的地」
        if (info.CropSeedRow != 0)
            badges.Add("Farm".Loc());
        if (info.FromPasture)
            badges.Add("Pasture".Loc());
        if (info.Handicraft)
            badges.Add("Handicraft".Loc());
        if (info.SeedOfCropRow != 0)
            badges.Add("Seed".Loc());
        return badges.Count > 0 ? string.Join(" / ", badges) : "";
    }

    private void DrawTooltip(MaterialLedgerRow row) {
        var sb = new StringBuilder();
        var info = row.Info;

        sb.Append(info.Name);
        if (info.CategoryName.Length > 0)
            sb.Append(" (").Append(info.CategoryName).Append(')');
        sb.Append('\n');
        if (!row.Unlocked)
            sb.Append("Not discovered yet.".Loc()).Append('\n');

        sb.Append('\n');
        if (_ledger.DemandKnown)
            sb.Append("Demand - cycle ??, week ??, week + next ??".Loc(row.Demand[0], row.Demand[1], row.Demand[2])).Append('\n');
        else
            sb.Append("Demand unknown - the workshop agenda has not been read this session.".Loc()).Append('\n');
        sb.Append(_ledger.StockKnown
            ? "In pouch: ??".Loc(row.Stock)
            : "Pouch count unknown.".Loc()).Append('\n');
        sb.Append("Incoming - granary ??, farm ??, pasture ??".Loc(
            _ledger.GranaryKnown ? row.Granary : "?",
            _ledger.FarmKnown ? row.Farm : "?",
            _ledger.PastureKnown ? row.Pasture : "?")).Append('\n');

        if (info.Gather is { } g) {
            sb.Append('\n');
            sb.Append("Gathering spot - x ??, z ?? (radius ??)".Loc((int)g.X, (int)g.Z, (int)g.Radius)).Append('\n');
            sb.Append("Tool: ??".Loc(g.ToolKeyItemRow == 0 ? "Bare hands".Loc() : g.ToolName)).Append('\n');
            if (g.Cave)
                sb.Append("Located in the cave.".Loc()).Append('\n');
        }

        if (info.RareExpeditionAreas.Count > 0 || info.ExpeditionAreas.Count > 0) {
            List<string> areas = [];
            foreach (var a in info.RareExpeditionAreas)
                areas.Add(MaterialSources.ExpeditionAreaName(a) + "*");
            foreach (var a in info.ExpeditionAreas)
                areas.Add(MaterialSources.ExpeditionAreaName(a));
            sb.Append("Granary expeditions: ??".Loc(string.Join(", ", areas))).Append('\n');
        }

        if (info.UsedByCrafts.Count > 0) {
            sb.Append('\n');
            sb.Append("Used by ?? workshop product(s):".Loc(info.UsedByCrafts.Count)).Append('\n');
            var shown = 0;
            foreach (var use in info.UsedByCrafts) {
                if (shown++ >= 12) {
                    sb.Append("  ...").Append('\n');
                    break;
                }
                sb.Append("  ").Append(use.CraftName).Append(" x").Append(use.Amount).Append('\n');
            }
        }

        if (info.UsedByRecipes.Count > 0) {
            sb.Append("Also used by island handicraft:".Loc()).Append('\n');
            var shown = 0;
            foreach (var use in info.UsedByRecipes) {
                if (shown++ >= 8) {
                    sb.Append("  ...").Append('\n');
                    break;
                }
                sb.Append("  ").Append(use.ProductName).Append(" x").Append(use.Amount);
                if (use.Wildcard)
                    sb.Append(" (").Append("any of the same category".Loc()).Append(')');
                sb.Append('\n');
            }
        }

        ImGui.SetTooltip(sb.ToString().TrimEnd('\n'));
    }
}
