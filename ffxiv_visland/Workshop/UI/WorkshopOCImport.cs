using Dalamud.Game;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using visland.Helpers;

namespace visland.Workshop;

public unsafe class WorkshopOCImport {
    public WorkshopSolver.Recs Recommendations = new();

    private readonly WorkshopConfig _config;
    private readonly WorkshopSeasonDB _seasonDB;
    private readonly ExcelSheet<MJICraftworksObject> _craftSheet;
    private readonly List<string> _botNames;
    private readonly List<string> _botNamesEnglish;
    private readonly ClipboardParser _parser;
    private readonly ScheduleApplier _applier = new();
    private readonly FavourReader _favourReader;
    private readonly List<Func<bool>> _pendingActions = [];
    private int _loadedSeason;
    private bool _loadedNextWeek;
    private WorkshopDayFiller.Report? _fillReport;
    private FavourPlacementReport? _favourReport;

    public WorkshopOCImport() {
        _config = Service.Config.Get<WorkshopConfig>();
        _seasonDB = new WorkshopSeasonDB();
        _craftSheet = MJICraftworksObject.Get();
        // Display names, in client language. Note that WithLanguage(English) is silently overridden
        // to the client language by our TC Lumina fork (the TC client only ships TraditionalChinese
        // EXD), so on TC these are the zh-TW item names - which is what we want to *show*. Anything
        // that has to match the English text OC posts on Discord must NOT use these; that is what
        // _botNamesEnglish (from the embedded mji-craft-map.json) is for.
        _botNames = [.. _craftSheet.Select(r => OSCHandler.OfficialNameToBotName(Item.GetRow(r.Item.RowId)!.Value.WithLanguage(ClientLanguage.English).Name.ExtractText()))];
        _botNamesEnglish = LoadEnglishBotNames(_craftSheet.Count);
        _parser = new(_craftSheet, _botNamesEnglish);
        _favourReader = new(_botNamesEnglish);
    }

    // English OC bot name per craft row id (index == MJICraftworksObject row id, "" when unmapped),
    // loaded from the embedded english-name -> craft-id map so clipboard import and /favors keep
    // working on clients whose game data has no English sheets (TC).
    private static List<string> LoadEnglishBotNames(int count) {
        var names = Enumerable.Repeat(string.Empty, count).ToList();
        try {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("visland.Workshop.Data.mji-craft-map.json")
                ?? throw new Exception("Embedded resource visland.Workshop.Data.mji-craft-map.json not found");
            using var reader = new StreamReader(stream);
            foreach (var prop in JObject.Parse(reader.ReadToEnd()).Properties()) {
                var id = prop.Value.Value<int>();
                while (names.Count <= id)
                    names.Add(string.Empty);
                names[id] = prop.Name;
            }
            Service.Log.Info($"Loaded {names.Count(n => n.Length > 0)} English craft names from mji-craft-map.json");
        }
        catch (Exception ex) {
            Service.Log.Error(ex, "Failed to load mji-craft-map.json - clipboard import and /favors will not match English names");
        }
        return names;
    }

    public void Update() {
        var numDone = _pendingActions.TakeWhile(f => f()).Count();
        _pendingActions.RemoveRange(0, numDone);
    }

    public void Draw() {
        using var globalDisable = ImRaii.Disabled(_pendingActions.Count > 0);

        var align = WorkshopSeasonDiagnostics.Capture(_seasonDB, _config.SeasonOffset);
        var thisSeason = align.ThisSeason;
        var nextSeason = align.NextSeason;
        ImGui.TextUnformatted("Archive seasons ??-?? (cycle ??)".Loc(_seasonDB.RangeStart, _seasonDB.RangeEnd, _seasonDB.CycleLength));
        ImGui.TextUnformatted("This week → Season ??".Loc(thisSeason) + (_seasonDB.TryGet(thisSeason, out var cur) ? $" ({cur.Date})" : $" ({"missing".Loc()})"));
        ImGui.TextUnformatted("Next week → Season ??".Loc(nextSeason) + (_seasonDB.TryGet(nextSeason, out var nxt) ? $" ({nxt.Date})" : $" ({"missing".Loc()})"));
        DrawSeasonAlignment(align);

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
        DrawFillReport();
        DrawFavourReport();

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
            // 剪貼簿匯入不會跑補天/請求整合,舊的報告留著就變成在描述一份已經不存在的排程。
            _fillReport = null;
            _favourReport = null;
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
        var season = _seasonDB.Shift(_seasonDB.CurrentSeason(nextWeek), _config.SeasonOffset);
        var archiveRecs = _seasonDB.BuildRecs(season);
        var applyFavours = favours != null && _config.FavourMode != FavourMode.None;

        // 「請求盡量排最早」必須先補天再放請求 —— 封存只有 C2~C7,不先補出 C1 與被釋放的休息日,
        // FavourIntegration 的 days 裡根本沒有更早的日子可以放。
        var earliestFirst = applyFavours && _config.FillEmptyDays && _config.FavoursEarliestCycles;

        _fillReport = null;
        _favourReport = null;

        // 對照組 = 「同樣的補天設定、但完全不放請求產品」的排程;請求的代價就是它跟最後結果的差。
        // earliestFirst 時它同時也是請求整合的輸入,所以不會多算一次。
        var withoutFavours = archiveRecs;
        if (_config.FillEmptyDays && (earliestFirst || applyFavours)) {
            withoutFavours = WorkshopDayFiller.Fill(archiveRecs, nextWeek, _craftSheet, out var preFill);
            if (earliestFirst)
                LogFillReport(preFill);
        }

        Recommendations = applyFavours
            ? FavourIntegration.Apply(earliestFirst ? withoutFavours : archiveRecs, _config.FavourMode, favours!.Value, _craftSheet, _seasonDB.RestCycles(season), earliestFirst)
            : archiveRecs;

        // 補封存沒給的生產日。預設順序刻意放在請求整合**之後**:請求模式(尤其 MinMaxFreeRestDay)
        // 自己會動封存的休息日,先跑它才知道最後到底哪幾天還是空的。
        // earliestFirst 時順序相反(上面已經補過),代價是補天解算器看不到請求會佔掉哪一格。
        if (_config.FillEmptyDays && !earliestFirst) {
            Recommendations = WorkshopDayFiller.Fill(Recommendations, nextWeek, _craftSheet, out var report);
            LogFillReport(report);
        }

        if (applyFavours) {
            _favourReport = FavourPlacementReport.Build(
                Recommendations, favours!.Value, _craftSheet, WorkshopUtils.GetMaxWorkshops(),
                WorkshopDayFiller.ScoreWeek(withoutFavours, nextWeek, _craftSheet),
                WorkshopDayFiller.ScoreWeek(Recommendations, nextWeek, _craftSheet),
                earliestFirst);
            Service.Log.Information(_favourReport.LogLine(_config.FavourMode));
        }

        _loadedSeason = season;
        _loadedNextWeek = nextWeek;
        Service.Log.Info($"Loaded workshop season {season} (favour mode {_config.FavourMode})");
        WorkshopSeasonDiagnostics.Log(WorkshopSeasonDiagnostics.Capture(_seasonDB, _config.SeasonOffset), _seasonDB);
    }

    private void LogFillReport(WorkshopDayFiller.Report report) {
        _fillReport = report;
        if (report.Filled)
            Service.Log.Information($"[fill-empty-days] filled {WorkshopDayFiller.FormatCycles(report.FilledCycles)} across {report.Workshops} workshop(s): " +
                $"added value {report.AddedValue:F0} vs archive average {report.ArchiveValuePerDay:F0}/day over {report.ArchiveDays} day(s) " +
                $"= {(report.ArchiveValuePerDay > 0 ? report.AddedValue / report.ArchiveValuePerDay : 0):F2} archive-days worth");
        else
            Service.Log.Information($"[fill-empty-days] nothing added: {report.SkipReason}");
    }

    // 季號對位:算出來的季號 vs 遊戲實際的受歡迎度列號。
    // 這兩個數字擺在一起,實機看一眼就知道相位有沒有對上 —— 離線沒有辦法判定(見 WorkshopSeasonDiagnostics)。
    // ⚠️ 資料還沒抓到時畫「?」不畫 0:把「不知道」畫成 0 會直接誤導。
    private static void DrawSeasonAlignment(WorkshopSeasonDiagnostics.Snapshot s) {
        var text = "In-game popularity row: this ?? / next ??".Loc(s.CurrentPopularityText, s.NextPopularityText);
        if (!s.DemandKnown)
            ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f), text + "  " + "(demand data not fetched yet)".Loc());
        else if (!s.NextFollowsCurrent)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), text + "  " + "(next is not this+1 - the 100-week cycle assumption may be wrong)".Loc());
        else
            ImGui.TextUnformatted(text + "  " + "(offset ??)".Loc(s.ImpliedOffset));
        ImGuiComponents.HelpMarker(
            "The archive season number is derived from the date alone and has never been checked against the game.".Loc() + "\n" +
            "If it is out of phase, the loaded schedule is the wrong season - it still earns cowries, just fewer, and nothing reports an error.".Loc() + "\n" +
            "These numbers are also written to the log at Information level on every archive load.".Loc() + "\n" +
            "Season offset in Settings shifts the season by whole weeks once you know the correct phase.".Loc());
    }

    // 補上的兩天值不值得,用同一把尺跟封存那幾天比。數字也會進 log。
    private void DrawFillReport() {
        if (_fillReport is not { } r)
            return;
        if (!r.Filled) {
            if (r.SkipReason != null)
                ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f), "Empty cycles not filled: ??".Loc(r.SkipReason));
            return;
        }
        var perDay = r.ArchiveValuePerDay > 0 ? r.AddedValue / (r.FilledCycles.Count * r.ArchiveValuePerDay) : 0;
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f),
            "Filled ?? with a local solve (~??x an archive day)".Loc(WorkshopDayFiller.FormatCycles(r.FilledCycles), perDay.ToString("F2")));
        ImGuiComponents.HelpMarker(
            "The Overseas Casuals archive only ever covers 5 production days; the game itself has no such limit.".Loc() + "\n" +
            "These cycles are solved locally from the game's own popularity and supply data, after accounting for what the archive days already produce.".Loc() + "\n" +
            "Relative value (not actual cowries): added ?? vs archive average ?? per day over ?? day(s), ?? workshop(s).".Loc(
                r.AddedValue.ToString("F0"), r.ArchiveValuePerDay.ToString("F0"), r.ArchiveDays, r.Workshops));
    }

    // 請求排在哪幾天、佔多少產能、代價多少。
    // 🔑 「請求排在哪幾天」與「有沒有全部達標」是要隨時掃視的 -> 放列上;
    //    逐日進度與絕對價值是起疑才查的 -> 放 tooltip。
    // ⚠️ 價值算不出來時列上畫「?」不畫 0 —— 把「不知道」畫成 0 會直接誤導。
    private void DrawFavourReport() {
        if (_favourReport is not { } r)
            return;
        if (!r.Valid) {
            ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f), "Request placement not analysed: ??".Loc(r.SkipReason ?? "?"));
            return;
        }
        if (r.Cycles.Count == 0) {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "No request items anywhere in this schedule".Loc());
            return;
        }

        if (r.AllMet)
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f),
                "Requests on ?? - ??/?? workshop-days - all met by ?? - week value ??".Loc(
                    r.CyclesText, r.OccupiedWorkshopDays, r.TotalWorkshopDays, r.CompleteByText, r.LossText));
        else
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                "Requests on ?? - ??/?? workshop-days - NOT all met this week - week value ??".Loc(
                    r.CyclesText, r.OccupiedWorkshopDays, r.TotalWorkshopDays, r.LossText));

        var value = r.ValueKnown
            ? "Relative value (not actual cowries): ?? without requests -> ?? with them.".Loc(r.ValueWithout.ToString("F0"), r.ValueWith.ToString("F0"))
            : "Week value could not be computed: ??".Loc(r.ValueSkipReason ?? "?");
        ImGuiComponents.HelpMarker(
            "Cumulative request output after each cycle (4h / 6h / 8h): ??".Loc(r.ProgressText()) + "\n" +
            "Totals for the week: ??".Loc(string.Join(", ", r.Total.Select((v, i) => $"{v}/{r.Targets[i]}"))) + "\n" +
            value + "\n" +
            "Moving requests earlier does not add production - it changes which workshop-day they take. The cost is the difference between the plan they replace and the request plan.".Loc() + "\n" +
            "The game gives the whole week to fill a request, so an early placement buys margin (material shortages, a disturbed schedule), not extra reward.".Loc() + "\n" +
            (r.EarliestFirst
                ? "Earliest-cycle placement is ON.".Loc()
                : "Earliest-cycle placement is OFF - requests only use the cycles the archive covers.".Loc()));
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
                            ImGui.Image(Service.TextureProvider.GetFromGameIcon(new GameIconLookup(craftworkItemIcon)).GetWrapOrEmpty().Handle, iconSizeVec, Vector2.Zero, Vector2.One);

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
