using Dalamud.Interface.Utility.Raii;
using visland.Helpers;
using ImGuiNET;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace visland.Workshop;

unsafe class WorkshopWindow : UIAttachedWindow {
    private readonly WorkshopConfig _config;
    private readonly WorkshopManual _manual = new();
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
            WorkshopUtils.VoidSecondRestThisWeek();
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
            FavourMode.MinMaxFreeRestDay => "Same as min-max, but turns the archive's second rest day into a crafting day (C1 stays rest) so most favours can land on a \"free\" day.".Loc(),
            _ => "",
        });

        ImGui.Separator();
        if (ImGui.Checkbox("Show advanced favour override controls".Loc(), ref _config.UseFavourSolver))
            _config.NotifyModified();
        ImGui.TextWrapped("Shows manual favour-solver / clipboard override buttons on the Schedule tab.".Loc());
    }
}
