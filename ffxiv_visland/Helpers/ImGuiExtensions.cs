using Dalamud.Interface;
using Dalamud.Interface.Components;
using ImGuiNET;

namespace visland.Helpers;

public static class ImGuiExtensions {
    extension(ImGui) {
        public static void TextV(string text) {
            ImGui.AlignTextToFramePadding();
            ImGui.Text(text);
        }

        public static bool IconButton(FontAwesomeIcon icon, string? tooltip = null) {
            var res = ImGuiComponents.IconButton(icon);
            if (res && tooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
            return res;
        }

        // TC/old-API-gen note: classic ImGuiNET has no InputUInt(ref uint) overload, only InputInt(ref int).
        public static bool InputUInt(string label, ref uint value) {
            var iv = (int)value;
            var changed = ImGui.InputInt(label, ref iv);
            if (changed && iv >= 0)
                value = (uint)iv;
            return changed && iv >= 0;
        }
    }
}
