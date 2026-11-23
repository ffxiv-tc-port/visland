using Dalamud.Bindings.ImGui;

namespace visland.Helpers;

/// <summary>
/// 寫死的設計像素依目前字級換算：值 × <c>ImGui.GetFontSize()</c> ÷ Dalamud 出廠字級。
/// <c>GetFontSize()</c> 已含全域縮放，所以不再乘 GlobalScale；出廠字級時 <c>Px(x)</c>＝x×GlobalScale。
/// 只在繪製期間呼叫（量的是當下推入的字型）。
/// </summary>
public static class UiScale {
    // visland 沒有初始化 ECommons，不能用 .Scale()；分母是出廠字級，用使用者字級當分母會抵銷放大。
    private const float FactoryFontSizePx = 12f * 4f / 3f;

    public static float Px(float designPixels) => designPixels * ImGui.GetFontSize() / FactoryFontSizePx;
}
