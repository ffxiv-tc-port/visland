using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace visland.Workshop;

internal unsafe class ClipboardParser(ExcelSheet<MJICraftworksObject> craftSheet, List<string> botNames) {
    public WorkshopSolver.Recs ParseRecs(string str) {
        var result = new WorkshopSolver.Recs();

        var curRec = new WorkshopSolver.DayRec();
        var nextSlot = 24;
        var curCycle = 0;
        foreach (var l in str.Split('\n', '\r')) {
            if (TryParseCycleStart(l, out var cycle)) {
                result.Add(curCycle > 0 ? curCycle : cycle - 1, curRec);
                curRec = new();
                nextSlot = 24;
                curCycle = cycle;
            }
            else if (l is "First 3 Workshops" or "All Workshops") {
                if (!curRec.Empty)
                    throw new Exception("Unexpected start of 1st workshop recs");
            }
            else if (l == "4th Workshop") {
                nextSlot = 24;
            }
            else if (TryParseItem(l) is var item && item != null) {
                if (nextSlot + item.Value.CraftingTime > 24) {
                    curRec.Workshops.Add(new());
                    nextSlot = 0;
                }
                curRec.Workshops.Last().Add(nextSlot, item.Value.RowId);
                nextSlot += item.Value.CraftingTime;
            }
            else
                Service.Log.Verbose($"Failed to parse {l}");
        }
        // 🔴 原本是 AgentMJICraftSchedule.Instance()->Data->CycleInProgress 兩層裸讀,而且只有在
        //    剪貼簿內容沒帶週期編號(curCycle <= 0)時才真的用得到。判空就擺在真正要用的那一刻:
        //    讀不到就丟一個講得清楚的例外,由既有的 ImportRecsFromClipboard try/catch 顯示給
        //    使用者(失敗形式=匯入失敗並說明原因,不是崩潰,也不是靜默排到錯的週期)。
        var lastCycle = curCycle;
        if (lastCycle <= 0) {
            var agent = AgentMJICraftSchedule.Instance();
            if (agent == null || agent->Data == null)
                throw new Exception("Craft schedule data is unavailable - open the workshop schedule window and try again");
            lastCycle = (agent->Data->CycleInProgress + 2) % 8;
        }
        result.Add(lastCycle, curRec);

        return result;
    }

    public List<WorkshopSolver.WorkshopRec> ParseRecOverrides(string str) {
        var result = new List<WorkshopSolver.WorkshopRec>();
        var nextSlot = 24;

        foreach (var l in str.Split('\n', '\r')) {
            if (l.StartsWith("Schedule #")) {
                nextSlot = 24;
            }
            else if (TryParseItem(l) is var item && item != null) {
                if (nextSlot + item.Value.CraftingTime > 24) {
                    result.Add(new());
                    nextSlot = 0;
                }
                result.Last().Add(nextSlot, item.Value.RowId);
                nextSlot += item.Value.CraftingTime;
            }
            else
                Service.Log.Verbose($"Failed to parse {l}");
        }

        return result;
    }

    private static bool TryParseCycleStart(string str, out int cycle) {
        if (str.StartsWith("Cycle "))
            return int.TryParse(str.AsSpan(6, 1), out cycle);
        if (str.StartsWith("Season ") && str.IndexOf(", Cycle ") is var cycleStart && cycleStart > 0)
            return int.TryParse(str.AsSpan(cycleStart + 8, 1), out cycle);
        cycle = 0;
        return false;
    }

    private MJICraftworksObject? TryParseItem(string line) {
        var matchingRows = botNames.Select((n, i) => (n, i)).Where(t => !string.IsNullOrEmpty(t.n) && IsMatch(line, t.n)).ToList();
        if (matchingRows.Count > 1) {
            matchingRows = [.. matchingRows.OrderByDescending(t => MatchingScore(t.n, line))];
            Service.Log.Info($"Row '{line}' matches {matchingRows.Count} items: {string.Join(", ", matchingRows.Select(r => r.n))}\n" +
                "First one is most likely the correct match. Please report if this is wrong.");
        }
        return matchingRows.Count > 0 ? craftSheet.GetRow((uint)matchingRows.First().i) : null;
    }

    private static bool IsMatch(string x, string y) => Regex.IsMatch(x, $@"\b{Regex.Escape(y)}\b");

    private static int MatchingScore(string item, string line) {
        var score = 0;
        if (line.Contains(item))
            score += item.Length;
        return score;
    }
}
