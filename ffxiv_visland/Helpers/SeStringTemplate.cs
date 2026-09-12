using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using System;
using System.Collections.Generic;
using System.Text;

namespace visland.Helpers;

/// <summary>把「含執行期巨集的 SeString 模板」拆成靜態片段，用來比對一則<b>已經帶入實際內容</b>的訊息。</summary>
/// <remarks>存在的理由：資料表裡的訊息模板常常含執行期才決定的內容（<see cref="MacroCode.String"/> 帶入角色名、<see cref="MacroCode.Num"/> 帶入數字…）。<see cref="ReadOnlySeString.ExtractText()"/>對這些巨集吐<b>空字串</b>，所以「模板 <c>ExtractText()</c> 的結果」永遠不會等於「實際收到的那一則訊息」—— 用 <c>==</c> 去比是<b>恆為 false</b>，而且不報錯。
/// 🔴 片段一律用<b>與比對對象同一套剝法</b>（Lumina <c>ExtractText()</c>）產生：連字符 payload（<see cref="MacroCode.Hyphen"/>）吐 U+002D、不斷行空格（<see cref="MacroCode.NonBreakingSpace"/>）吐 U+00A0、換行吐<see cref="Environment.NewLine"/>。用 Dalamud <c>SeString.TextValue</c>（連字符吐 U+2013）或 ECommons <c>GetText()</c>（連字符整個丟掉）產生片段，含連字符的模板會再一次恆不相等。
/// </remarks>
public static class SeStringTemplate {
    /// <summary>
    /// 一個模板至少要有這麼多個靜態字元才准用來比對。
    /// </summary>
    /// <remarks>
    /// 🔴 這道閘門不是效能考量，是<b>避免誤判</b>：靜態片段只剩「。」之類的模板會命中幾乎任何訊息，
    /// 那會變成「隨便一則錯誤訊息都把路線停掉」。寧可這一列不偵測，也不要誤停。
    /// </remarks>
    public const int MinStaticChars = 4;

    /// <summary>Lumina <c>ExtractText()</c> 對 <see cref="MacroCode.NonBreakingSpace"/> 吐的字元（U+00A0）。</summary>
    /// <remarks>寫成跳脫序列而不是字面值：那個字元在原始碼裡看不見，被工具正規化成半形空格時沒有人會發現。</remarks>
    private const string NonBreakingSpace = "\u00A0";

    /// <summary>一條模板拆出來的可比對形狀。</summary>
    public sealed class Shape {
        /// <summary>依出現順序排列的靜態片段，全部非空。</summary>
        public string[] Segments { get; }

        /// <summary>模板的開頭就是靜態文字（不是巨集）⇒ 訊息必須以第一個片段開頭。</summary>
        public bool AnchorStart { get; }

        /// <summary>模板的結尾就是靜態文字（不是巨集）⇒ 訊息必須以最後一個片段結尾。</summary>
        public bool AnchorEnd { get; }

        internal Shape(string[] segments, bool anchorStart, bool anchorEnd) {
            Segments = segments;
            AnchorStart = anchorStart;
            AnchorEnd = anchorEnd;
        }

        /// <summary>這一則訊息是不是這條模板帶入內容之後的樣子。</summary>
        public bool Matches(string message) {
            if (Segments.Length == 0)
                return false;

            // 模板整條都是靜態文字（完全沒有巨集）⇒ 維持「完全相等」，不放寬半步。
            // 這一支是刻意的：本來就比得中的那些列，改動後的行為必須逐字不變。
            if (AnchorStart && AnchorEnd && Segments.Length == 1)
                return string.Equals(message, Segments[0], StringComparison.Ordinal);

            if (AnchorStart && !message.StartsWith(Segments[0], StringComparison.Ordinal))
                return false;
            if (AnchorEnd && !message.EndsWith(Segments[^1], StringComparison.Ordinal))
                return false;

            // 依序包含全部片段。最左貪心對「有序子串序列」是最佳解，不會漏掉可成立的配置。
            var at = 0;
            foreach (var seg in Segments) {
                var hit = message.IndexOf(seg, at, StringComparison.Ordinal);
                if (hit < 0)
                    return false;
                at = hit + seg.Length;
            }

            return true;
        }

        /// <summary>寫進記錄用的可讀形狀，例如 <c>…「的背包已滿，無法進行捕魚作業。」$</c>。</summary>
        public override string ToString() {
            var sb = new StringBuilder();
            sb.Append(AnchorStart ? "^" : "…");
            for (var i = 0; i < Segments.Length; i++) {
                if (i > 0)
                    sb.Append('…');
                sb.Append('「').Append(Segments[i]).Append('」');
            }

            sb.Append(AnchorEnd ? "$" : "…");
            return sb.ToString();
        }
    }

    /// <summary>把一條模板拆成可比對形狀；靜態文字不足以辨識時回 <c>null</c>。</summary>
    public static Shape? TryBuild(ReadOnlySeString template) {
        var segments = new List<string>();
        var sb = new StringBuilder();
        var anchorStart = false;
        var anchorEnd = false;
        var first = true;

        foreach (var payload in template) {
            var piece = Render(payload);
            if (piece.Length > 0) {
                if (first)
                    anchorStart = true;
                anchorEnd = true;
                sb.Append(piece);
            } else {
                // 這個 payload 在 ExtractText() 下吐空字串 ⇒ 它是一個「洞」：
                // 執行期可能塞進任意內容（角色名、數字），也可能真的什麼都沒有。
                anchorEnd = false;
                if (sb.Length > 0) {
                    segments.Add(sb.ToString());
                    sb.Clear();
                }
            }

            first = false;
        }

        if (sb.Length > 0)
            segments.Add(sb.ToString());

        var total = 0;
        foreach (var s in segments)
            total += s.Length;

        return segments.Count > 0 && total >= MinStaticChars
            ? new Shape([.. segments], anchorStart, anchorEnd)
            : null;
    }

    /// <summary>離線語料用的單次入口：模板原始位元組 ＋ 訊息 → 命中與否。參數與回傳都只用 BCL 型別。</summary>
    public static bool MatchesTemplate(byte[] templateBytes, string message)
        => TryBuild(new ReadOnlySeString(templateBytes))?.Matches(message) ?? false;

    /// <summary>離線語料用的單次入口：模板原始位元組 → 拆出來的片段（拆不出來時回空陣列）。</summary>
    public static string[] DescribeTemplate(byte[] templateBytes)
        => TryBuild(new ReadOnlySeString(templateBytes))?.Segments ?? [];

    /// <summary>
    /// 單一 payload 在 <see cref="ReadOnlySeString.ExtractText()"/> 下的貢獻。
    /// </summary>
    /// <remarks>
    /// 🔴 這幾條分支<b>必須</b>與 Lumina <c>ExtractText(useSoftHyphen: false, macroPlaceholder: "")</c>
    /// 逐字一致，否則拆出來的片段不是「比對對象的子串」。離線語料有一道閘門直接驗這件事：
    /// 把拆出來的片段接起來必須<b>位元組同一</b>於整條模板的 <c>ExtractText()</c>。
    /// </remarks>
    private static string Render(ReadOnlySePayload payload) {
        if (payload.Type == ReadOnlySePayloadType.Text)
            return Encoding.UTF8.GetString(payload.Body.Span);

        if (payload.Type != ReadOnlySePayloadType.Macro)
            return string.Empty;

        return payload.MacroCode switch {
            MacroCode.NewLine => Environment.NewLine,
            MacroCode.NonBreakingSpace => NonBreakingSpace,
            MacroCode.Hyphen => "-",
            // MacroCode.SoftHyphen 在 useSoftHyphen: false 下吐空字串 —— 與 default 同路。
            _ => string.Empty,
        };
    }
}
