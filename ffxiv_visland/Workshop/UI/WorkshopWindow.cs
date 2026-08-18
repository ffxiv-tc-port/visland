using Dalamud.Interface.Utility;
using System;
using Dalamud.Interface.Utility.Raii;
using visland.Helpers;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace visland.Workshop;

unsafe class WorkshopWindow : UIAttachedWindow {
    private readonly WorkshopConfig _config;
    private readonly WorkshopManual _manual = new();
    private readonly WorkshopRest _rest = new();
    private readonly WorkshopOCImport _oc = new();
    private readonly WorkshopDebug _debug = new();

    public WorkshopWindow() : base("Workshop automation".Loc(), "MJICraftSchedule", new(500, 650)) {
        _config = Service.Config.Get<WorkshopConfig>();
    }

    public override void PreOpenCheck() {
        base.PreOpenCheck();
        var agent = AgentMJICraftSchedule.Instance();
        IsOpen &= agent != null && agent->Data != null;

        _oc.Update();
    }

    public override void Draw() {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs) {
            using (var tab = ImRaii.TabItem("Schedule".Loc()))
                if (tab)
                    _oc.Draw();
            using (var tab = ImRaii.TabItem("Manual schedule".Loc()))
                if (tab)
                    _manual.Draw();
            using (var tab = ImRaii.TabItem("Rest days".Loc()))
                if (tab)
                    _rest.Draw();
            using (var tab = ImRaii.TabItem("Settings".Loc()))
                if (tab)
                    DrawSettings();
            using (var tab = ImRaii.TabItem("Debug"))
                if (tab)
                    _debug.Draw();
        }
    }

    public override void OnOpen() {
        if (_config.AutoOpenNextDay) {
            WorkshopUtils.SetCurrentCycle(AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 1);
        }
        if (_config.FavourMode == FavourMode.MinMaxFreeRestDay)
            WorkshopUtils.RelaxSecondRestThisWeek();
        if (_config.AutoImport)
            _oc.LoadSeasonRecs(false, silent: true);
    }

    private void DrawSettings() {
        if (ImGui.Checkbox("Automatically select next cycle on open".Loc(), ref _config.AutoOpenNextDay))
            _config.NotifyModified();
        if (ImGui.Checkbox("Automatically load archive recs on open".Loc(), ref _config.AutoImport))
            _config.NotifyModified();

        ImGui.Separator();
        ImGui.TextUnformatted("Favour integration".Loc());
        var mode = (int)_config.FavourMode;
        var modes = new[] {
            "None — OC schedule only".Loc(),
            "Replace workshop 4 — credit favours already in WS1-3".Loc(),
            "Min-max — substitutions + sacrifice low-value slots".Loc(),
            "Min-max + free rest day — craft on OC's second rest day".Loc(),
        };
        if (ImGui.Combo("##favourMode", ref mode, modes, modes.Length)) {
            _config.FavourMode = (FavourMode)mode;
            _config.NotifyModified();
        }
        ImGui.TextWrapped(_config.FavourMode switch {
            FavourMode.None => "Loads the archived Overseas Casuals schedule as-is. Use manual favour overrides if needed.".Loc(),
            FavourMode.ReplaceWorkshop4 => "Workshops 1-3 keep the archive schedule. Workshop 4 is filled from the built-in favour solver, after crediting any favour crafts already produced by the recommended agenda.".Loc(),
            FavourMode.MinMax => "Tries same-duration/category substitutions first, then places remaining favours on the lowest-value workshop slots so high-cowrie days stay intact when possible.".Loc(),
            FavourMode.MinMaxFreeRestDay => "Same as min-max, but turns the archive's second rest day into a crafting day (the earliest rest day stays) so most favours can land on a \"free\" day. Only this week's rest days are touched, and rest days are only ever removed, never added.".Loc(),
            _ => "",
        });

        ImGui.Separator();
        ImGui.TextUnformatted("Empty cycles".Loc());
        if (ImGui.Checkbox("Fill the cycles the archive leaves empty".Loc(), ref _config.FillEmptyDays))
            _config.NotifyModified();
        ImGui.TextWrapped("The Overseas Casuals archive only covers 5 production days per season. The game itself allows crafting on all 7 (rest days are a rule of the native UI, not of the game). When enabled, the remaining cycles are solved locally from the game's own popularity and supply data, counting what the archive days already produce so the same items are not picked twice.".Loc());

        using (ImRaii.Disabled(!_config.FillEmptyDays)) {
            var surplus = _config.SurplusPreferencePercent;
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("Prefer surplus materials".Loc(), ref surplus, 0, 50, "%d%%")) {
                _config.SurplusPreferencePercent = Math.Clamp(surplus, 0, 50);
                _config.NotifyModified();
            }
        }
        ImGui.TextWrapped("Biases the local solve towards products whose materials you already have spare - spare meaning pouch stock above what the workshop agenda needs over the next two weeks, the same measure the granary reassignment and the material ledger use. 0% ignores materials entirely, which is the previous behaviour. Higher values trade expected cowries for using up what is piling up; the report on the Schedule tab shows what the filled cycles are worth either way, so you can see the cost. What the archive days already consume is subtracted first, and each workshop sees what the previous one used up. If pouch counts or the agenda cannot be read the preference is skipped and the cycles are solved on value alone.".Loc());

        using (ImRaii.Disabled(!_config.FillEmptyDays || _config.FavourMode == FavourMode.None)) {
            if (ImGui.Checkbox("Put requests on the earliest cycles".Loc(), ref _config.FavoursEarliestCycles))
                _config.NotifyModified();
        }
        ImGui.TextWrapped("Requests are placed from cycle 1 onwards instead of only on the cycles the archive covers. Needs \"Fill the cycles the archive leaves empty\" and a favour mode other than None - the archive starts at cycle 2, so without the filled cycles there is no earlier day to use. In the min-max modes this replaces the value-based day ordering with plain earliest-first.".Loc());
        ImGui.TextWrapped("The game gives you the whole week to fill a request, so this does not earn more; it buys margin and shows the cost on the Schedule tab.".Loc());

        ImGui.Separator();
        ImGui.TextUnformatted("Season alignment".Loc());
        var offset = _config.SeasonOffset;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Season offset (weeks)".Loc(), ref offset)) {
            _config.SeasonOffset = offset;
            _config.NotifyModified();
        }
        ImGui.TextWrapped("Shifts which archive season is loaded, in whole weeks. Leave at 0 unless the Schedule tab's alignment readout shows the computed season is out of phase with the game.".Loc());

        ImGui.Separator();
        if (ImGui.Checkbox("Show advanced favour override controls".Loc(), ref _config.UseFavourSolver))
            _config.NotifyModified();
        ImGui.TextWrapped("Shows manual favour-solver / clipboard override buttons on the Schedule tab.".Loc());
    }
}
