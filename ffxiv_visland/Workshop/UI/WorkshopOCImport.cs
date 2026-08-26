using Dalamud.Game;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;

namespace visland.Workshop;

public unsafe class WorkshopOCImport {
    public WorkshopSolver.Recs Recommendations = new();

    private readonly WorkshopConfig _config;
    private readonly WorkshopSeasonDB _seasonDB;
    private readonly ExcelSheet<MJICraftworksObject> _craftSheet;
    private readonly List<string> _botNames;
    private readonly ClipboardParser _parser;
    private readonly ScheduleApplier _applier = new();
    private readonly FavourReader _favourReader;
    private readonly List<Func<bool>> _pendingActions = [];
    private int _loadedSeason;
    private bool _loadedNextWeek;

    public WorkshopOCImport() {
        _config = Service.Config.Get<WorkshopConfig>();
        _seasonDB = new WorkshopSeasonDB();
        _craftSheet = MJICraftworksObject.Get();
        _botNames = [.. _craftSheet.Select(r => OSCHandler.OfficialNameToBotName(Item.GetRow(r.Item.RowId)!.Value.WithLanguage(ClientLanguage.English).Name.ExtractText()))];
        _parser = new(_craftSheet, _botNames);
        _favourReader = new(_botNames);
    }

    public void Update() {
        var numDone = _pendingActions.TakeWhile(f => f()).Count();
        _pendingActions.RemoveRange(0, numDone);
    }

    public void Draw() {
        using var globalDisable = ImRaii.Disabled(_pendingActions.Count > 0);

        var thisSeason = _seasonDB.CurrentSeason(false);
        var nextSeason = _seasonDB.CurrentSeason(true);
        ImGui.TextUnformatted("Archive seasons ??-?? (cycle ??)".Loc(_seasonDB.RangeStart, _seasonDB.RangeEnd, _seasonDB.CycleLength));
        ImGui.TextUnformatted("This week → Season ??".Loc(thisSeason) + (_seasonDB.TryGet(thisSeason, out var cur) ? $" ({cur.Date})" : $" ({"missing".Loc()})"));
        ImGui.TextUnformatted("Next week → Season ??".Loc(nextSeason) + (_seasonDB.TryGet(nextSeason, out var nxt) ? $" ({nxt.Date})" : $" ({"missing".Loc()})"));

        if (ImGui.Button("Load This Week".Loc()))
            LoadSeasonRecs(false);
        ImGui.SameLine();
        if (ImGui.Button("Load Next Week".Loc()))
            LoadSeasonRecs(true);
        ImGuiComponents.HelpMarker("Loads Overseas Casuals archive recommendations for the mapped season, then applies the favour mode from Settings.".Loc());

        if (ImGui.Button("Import Recommendations From Clipboard".Loc()))
            ImportRecsFromClipboard(false);
        ImGuiComponents.HelpMarker("Legacy importer for schedules copied from Discord.".Loc() + "\n" +
                        "The importer detects item names (without \"Isleworks\" et al) on each line.".Loc() + "\n" +
                        "You can copy an entire workshop schedule from discord, junk included.".Loc());

        if (Recommendations.Empty)
            return;

        if (_loadedSeason != 0)
            ImGui.TextUnformatted("Loaded season ??".Loc(_loadedSeason) + $" ({(_loadedNextWeek ? "next week" : "this week").Loc()})");

        ImGui.Separator();

        if (_config.UseFavourSolver) {
            ImGui.TextUnformatted("Advanced favour overrides".Loc());
            ImGuiComponents.HelpMarker("Manual overrides for the currently loaded schedule. Archive loads already apply the favour mode from Settings.".Loc());

            ImGui.TextV("Override 4th workshop with favours:".Loc());
            ImGui.SameLine();
            if (ImGui.Button("This Week".Loc() + "##4th"))
                OverrideSideRecsLastWorkshopSolver(false);
            ImGui.SameLine();
            if (ImGui.Button("Next Week".Loc() + "##4th"))
                OverrideSideRecsLastWorkshopSolver(true);

            ImGui.TextV("Override closest workshops with favours:".Loc());
            ImGui.SameLine();
            if (ImGui.Button("This Week".Loc() + "##asap"))
                OverrideSideRecsAsapSolver(false);
            ImGui.SameLine();
            if (ImGui.Button("Next Week".Loc() + "##asap"))
                OverrideSideRecsAsapSolver(true);

            if (ImGui.Button("Override 4th workshop from clipboard".Loc()))
                OverrideSideRecsLastWorkshopClipboard();
            if (ImGui.Button("Override closest workshops from clipboard".Loc()))
                OverrideSideRecsAsapClipboard();

            if (ImGuiComponents.IconButtonWithText(Dalamud.Interface.FontAwesomeIcon.Clipboard, "Copy /favors (this week)".Loc())) {
                try {
                    ImGui.SetClipboardText(_favourReader.CreateFavourRequestCommand(false));
                }
                catch (Exception ex) {
                    ReportError(ex.Message);
                }
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(Dalamud.Interface.FontAwesomeIcon.Clipboard, "Copy /favors (next week)".Loc())) {
                try {
                    ImGui.SetClipboardText(_favourReader.CreateFavourRequestCommand(true));
                }
                catch (Exception ex) {
                    ReportError(ex.Message);
                }
            }

            ImGui.Separator();
        }

        ImGui.TextV("Set Schedule:".Loc());
        ImGui.SameLine();
        if (ImGui.Button("This Week".Loc()))
            ApplyRecommendations(false);
        ImGui.SameLine();
        if (ImGui.Button("Next Week".Loc()))
            ApplyRecommendations(true);
        ImGui.SameLine();
        var ignoreFourth = _applier.IgnoreFourthWorkshop;
        if (ImGui.Checkbox("Ignore 4th Workshop".Loc(), ref ignoreFourth))
            _applier.IgnoreFourthWorkshop = ignoreFourth;
        ImGui.Separator();

        DrawCycleRecommendations();
    }

    public void ImportRecsFromClipboard(bool silent) {
        try {
            Recommendations = _parser.ParseRecs(ImGui.GetClipboardText());
            _loadedSeason = 0;
        }
        catch (Exception ex) {
            ReportError("Error: ??".Loc(ex.Message), silent);
        }
    }

    public void LoadSeasonRecs(bool nextWeek, bool silent = false) {
        try {
            if (_config.FavourMode == FavourMode.None) {
                ApplySeason(nextWeek, null);
                return;
            }

            _favourReader.EnsureDemandFavoursAvailable(_pendingActions);
            _pendingActions.Add(() => {
                try {
                    ApplySeason(nextWeek, _favourReader.ReadFavourState(nextWeek));
                }
                catch (Exception ex) {
                    ReportError("Error: ??".Loc(ex.Message), silent);
                }
                return true;
            });
        }
        catch (Exception ex) {
            ReportError("Error: ??".Loc(ex.Message), silent);
        }
    }

    private void ApplySeason(bool nextWeek, WorkshopSolver.FavourState? favours) {
        var season = _seasonDB.CurrentSeason(nextWeek);
        var baseRecs = _seasonDB.BuildRecs(season);
        Recommendations = favours == null || _config.FavourMode == FavourMode.None
            ? baseRecs
            : FavourIntegration.Apply(baseRecs, _config.FavourMode, favours.Value, _craftSheet, _seasonDB.RestCycles(season));
        _loadedSeason = season;
        _loadedNextWeek = nextWeek;
        Service.Log.Info($"Loaded workshop season {season} (favour mode {_config.FavourMode})");
    }

    private void DrawCycleRecommendations() {
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoKeepColumnsVisible;
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();

        using var scrollSection = ImRaii.Child("ScrollableSection");
        foreach ((var c, var r) in Recommendations.Enumerate()) {
            ImGui.TextV("Cycle ??:".Loc(c));
            ImGui.SameLine();
            if (ImGui.Button("Set on Active Cycle".Loc() + $"##{c}"))
                _applier.ApplyRecommendationToCurrentCycle(r);

            using var outerTable = ImRaii.Table($"table_{c}", r.Workshops.Count, tableFlags);
            if (outerTable) {
                var workshopLimit = r.Workshops.Count - (_applier.IgnoreFourthWorkshop && r.Workshops.Count > 1 ? 1 : 0);
                if (r.Workshops.Count <= 1) {
                    ImGui.TableSetupColumn(_applier.IgnoreFourthWorkshop ? "Workshops 1-??".Loc(maxWorkshops - 1) : "All Workshops".Loc());
                }
                else if (r.Workshops.Count < maxWorkshops) {
                    var numDuplicates = 1 + maxWorkshops - r.Workshops.Count;
                    ImGui.TableSetupColumn("Workshops 1-??".Loc(numDuplicates));
                    for (var i = 1; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn("Workshop ??".Loc(i + numDuplicates));
                }
                else {
                    for (var i = 0; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn("Workshop ??".Loc(i + 1));
                }
                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                for (var i = 0; i < workshopLimit; ++i) {
                    ImGui.TableNextColumn();
                    using var innerTable = ImRaii.Table($"table_{c}_{i}", 2, tableFlags);
                    if (innerTable) {
                        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                        foreach (var rec in r.Workshops[i].Slots) {
                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();
                            var iconSize = ImGui.GetTextLineHeight() * 1.5f;
                            var iconSizeVec = new Vector2(iconSize, iconSize);
                            var craftworkItemIcon = _craftSheet.GetRow(rec.CraftObjectId)!.Item.Value!.Icon;
                            ImGui.Image(Service.TextureProvider.GetFromGameIcon(new GameIconLookup(craftworkItemIcon)).GetWrapOrEmpty().ImGuiHandle, iconSizeVec, Vector2.Zero, Vector2.One);

                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(_botNames[(int)rec.CraftObjectId]);
                        }
                    }
                }
            }
        }
    }

    private void OverrideSideRecsLastWorkshopClipboard() {
        try {
            var overrideRecs = _parser.ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count)
                throw new Exception("Override list is longer than base schedule: ?? > ??".Loc(overrideRecs.Count, Recommendations.Schedules.Count));
            OverrideSideRecsLastWorkshop(overrideRecs);
        }
        catch (Exception ex) {
            ReportError("Error: ??".Loc(ex.Message));
        }
    }

    private void OverrideSideRecsLastWorkshopSolver(bool nextWeek) {
        _favourReader.EnsureDemandFavoursAvailable(_pendingActions);
        _pendingActions.Add(() => {
            OverrideSideRecsLastWorkshop(_favourReader.SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsLastWorkshop(List<WorkshopSolver.WorkshopRec> overrides) {
        foreach ((var r, var o) in Recommendations.Schedules.Zip(overrides)) {
            if (r.Workshops.Count > 1)
                r.Workshops.RemoveAt(r.Workshops.Count - 1);
            r.Workshops.Add(o);
        }
        if (overrides.Count > Recommendations.Schedules.Count)
            Service.ChatGui.Print("Warning: couldn't fit all overrides into base schedule".Loc(), "visland");
    }

    private void OverrideSideRecsAsapClipboard() {
        try {
            var overrideRecs = _parser.ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count * 4)
                throw new Exception("Override list is longer than base schedule: ?? > 4 * ??".Loc(overrideRecs.Count, Recommendations.Schedules.Count));
            OverrideSideRecsAsap(overrideRecs);
        }
        catch (Exception ex) {
            ReportError("Error: ??".Loc(ex.Message));
        }
    }

    private void OverrideSideRecsAsapSolver(bool nextWeek) {
        _favourReader.EnsureDemandFavoursAvailable(_pendingActions);
        _pendingActions.Add(() => {
            OverrideSideRecsAsap(_favourReader.SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsAsap(List<WorkshopSolver.WorkshopRec> overrides) {
        var nextOverride = 0;
        foreach (var r in Recommendations.Schedules) {
            var batchSize = Math.Min(4, overrides.Count - nextOverride);
            if (batchSize == 0)
                break;

            if (r.Workshops.Count > 1)
                r.Workshops.RemoveAt(r.Workshops.Count - 1);
            var maxLeft = 4 - batchSize;
            if (r.Workshops.Count > maxLeft)
                r.Workshops.RemoveRange(maxLeft, r.Workshops.Count - maxLeft);
            r.Workshops.AddRange(overrides.Skip(nextOverride).Take(batchSize));
            nextOverride += batchSize;
        }
        if (nextOverride < overrides.Count)
            Service.ChatGui.Print("Warning: couldn't fit all overrides into base schedule".Loc(), "visland");
    }

    private void ApplyRecommendations(bool nextWeek) {
        try {
            _applier.ApplyRecommendations(Recommendations, nextWeek);
        }
        catch (Exception ex) {
            ReportError("Error: ??".Loc(ex.Message));
        }
    }

    private static void ReportError(string msg, bool silent = false) {
        Service.Log.Error(msg);
        if (!silent)
            Service.ChatGui.PrintError(msg);
    }
}
