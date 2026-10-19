namespace visland;

// 跨檔逐字重複的使用者可見字串收斂處。
// 🔴 .Loc() 是**用英文原文當 key** 去查 LanguageChineseTraditional.ini。同一句被複製到兩個檔時,
//    改動其中一份的英文會讓**那一份**靜默退回英文,另一份照樣是中文;集中成常數,key 永遠只有一個。
// ⚠️ 這裡只放**真的出現在兩個以上位置**的字串;只用一次的字串留在使用處比較好讀。
public static class HelpText {
    public const string GranaryTopUpLowStock = "\"Top up low stock\" ranks the materials a granary can actually bring by pouch stock minus the workshop agenda's two-week demand, so a material the workshop is about to eat counts as scarcer than its raw count suggests. Materials the workshop does not use keep their plain stock and are still ranked. The first granary is sent wherever it can restock the scarcest one; the second gets whatever the first does not cover. It only counts whether a material is covered, not how much arrives - daily yields are not in the game data. If the workshop agenda has not been read yet, ranking falls back to plain pouch stock. Incoming granary and farm deliveries are deliberately not subtracted.";
    public const string ExportRespectWorkshopNeedsHelp = "Raises each limit to whatever the workshop agenda still needs: sells down to the larger of the limit above and (two-week requirement minus what is already inbound from granary, farm and pasture). If that requirement cannot be read, the plain limit is used - a missing reading never blocks a sale.";
    public const string ExportRespectWorkshopNeedsLabel = "Keep what the workshop agenda still needs";
    public const string AgendaNotReadYet = "The workshop agenda has not been read yet - open the Craftworks agenda once and the numbers will stay.";
    public const string DemandBreakdown = "Demand - cycle ??, week ??, week + next ??";
    public const string IncomingBreakdown = "Incoming - granary ??, farm ??, pasture ??";
    public const string DemandFallbackToPlainStock = "Workshop demand has not been read yet, so ranking falls back to plain pouch stock.";
    public const string OverridesDidNotFit = "Warning: couldn't fit all overrides into base schedule";
}
