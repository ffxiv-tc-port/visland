using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Text.RegularExpressions;

namespace visland.Gathering.AutoGather;

public static unsafe partial class GatheringAddon {
    public sealed class Gathering {
        private readonly AddonGathering* _addon;

        public Gathering(nint addon) => _addon = (AddonGathering*)addon;
        public Gathering(void* addon) => _addon = (AddonGathering*)addon;

        public int CurrentIntegrity => ParseFirstInt(_addon->GetTextNodeById(9)->NodeText.ToString());
        public int TotalIntegrity => ParseFirstInt(_addon->GetTextNodeById(12)->NodeText.ToString());

        public GatheredItem GetItem(int index) => new(this, index);
        public GatheredItem[] Items {
            get {
                var items = new GatheredItem[8];
                for (var i = 0; i < items.Length; i++)
                    items[i] = GetItem(i);
                return items;
            }
        }

        public void Gather(int index) {
            // CheckBoxEnabled 只有在「已確認可勾選」時才回 true；讀不到（元件或 OwnerNode 還沒建好）
            // 回 null，一樣不送 callback——失敗形式是「這一幀不做事」，下一幀重試。
            if (CheckBoxEnabled(CheckboxAt(index)) != true)
                return;
            var values = stackalloc AtkValue[2];
            values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            values[0].Int = 2;
            values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
            values[1].UInt = (uint)index;
            ((AtkUnitBase*)_addon)->FireCallback(1, values);
        }

        private AtkComponentCheckBox* CheckboxAt(int index) => index switch {
            0 => _addon->GatheredItemComponentCheckbox[0],
            1 => _addon->GatheredItemComponentCheckbox[1],
            2 => _addon->GatheredItemComponentCheckbox[2],
            3 => _addon->GatheredItemComponentCheckbox[3],
            4 => _addon->GatheredItemComponentCheckbox[4],
            5 => _addon->GatheredItemComponentCheckbox[5],
            6 => _addon->GatheredItemComponentCheckbox[6],
            7 => _addon->GatheredItemComponentCheckbox[7],
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        /// <summary>
        /// 安全地讀取採集項目核取方塊的「可否勾選」狀態。
        /// <para><c>null</c> ＝ <b>現在讀不到</b>（元件指標或 OwnerNode 還沒建好），
        /// <b>不代表</b>「已確認為停用」——判斷式要顯式區分這兩者。</para>
        /// <para>🔴 CS 的 <c>AtkComponentButton.IsEnabled</c> 是
        /// <c>AtkComponentBase.OwnerNode-&gt;AtkResNode.NodeFlags.HasFlag(...)</c>：
        /// 它解的是 <c>+0xA8</c> 的 <c>OwnerNode</c>（不是 <c>+0xA0</c> 的 <c>AtkResNode</c>），
        /// 而且對它零 null 檢查。AVE 是 .NET Core 的 corrupted-state exception，
        /// <c>try/catch</c> 完全攔不到，只能在讀取前擋下來。</para>
        /// </summary>
        internal static bool? CheckBoxEnabled(AtkComponentCheckBox* checkbox) {
            if (checkbox == null) return null;
            if (checkbox->AtkComponentButton.AtkComponentBase.OwnerNode == null) return null;
            return checkbox->AtkComponentButton.IsEnabled;
        }

        public sealed class GatheredItem(Gathering owner, int index) {
            private AtkComponentCheckBox* CheckBox => owner.CheckboxAt(index);

            /// <summary>三態版本：<c>null</c> ＝ 現在讀不到，<b>不是</b>「已確認停用」。</summary>
            public bool? IsEnabledOrUnknown => CheckBoxEnabled(CheckBox);

            /// <summary>只有「已確認可勾選」才回 <c>true</c>；讀不到一律 <c>false</c>（＝這次不動作）。</summary>
            public bool IsEnabled => IsEnabledOrUnknown == true;

            public string ItemName => TextOf(23);
            public uint ItemID => owner._addon->ItemIds[index];
            public bool IsCollectable => Service.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(ItemID)?.IsCollectable ?? false;
            public int ItemLevel => ParseFirstInt(TextOf(21));
            public int GatherChance => ParseFirstInt(TextOf(10));
            public int BoonChance => ParseFirstInt(TextOf(16));
            public void Gather() => owner.Gather(index);

            /// <summary>
            /// 讀元件底下某個文字節點。整條 <c>CheckBox-&gt;GetTextNodeById-&gt;GetAsAtkTextNode</c>
            /// 都是原生呼叫，任一層是 null 都會 AVE，所以逐層驗；讀不到就回空字串
            /// （<c>ParseFirstInt</c> 對空字串本來就回 0，和「文字裡沒有數字」同一條既有路徑）。
            /// </summary>
            private string TextOf(uint nodeId) {
                var checkbox = CheckBox;
                if (checkbox == null) return string.Empty;
                var node = checkbox->GetTextNodeById(nodeId);
                if (node == null) return string.Empty;
                var text = node->AtkResNode.GetAsAtkTextNode();
                return text == null ? string.Empty : text->NodeText.ToString();
            }
        }
    }

    public sealed class GatheringMasterpiece {
        private readonly AddonGatheringMasterpiece* _addon;

        public GatheringMasterpiece(nint addon) => _addon = (AddonGatheringMasterpiece*)addon;
        public GatheringMasterpiece(void* addon) => _addon = (AddonGatheringMasterpiece*)addon;

        private AtkUnitBase* Unit => (AtkUnitBase*)_addon;

        public string ItemName => _addon->ItemName->NodeText.ToString();
        public uint ItemID => Unit->AtkValues[2].UInt;
        public int CurrentCollectability => Unit->AtkValues[13].Int;
        public int MaxCollectability => Unit->AtkValues[14].Int;
        public uint CurrentIntegrity => Unit->AtkValues[62].UInt;
        public uint TotalIntegrity => Unit->AtkValues[63].UInt;
        public int ScourPower => Unit->AtkValues[48].Int;
        public int BrazenPowerMin => Unit->AtkValues[49].Int;
        public int BrazenPowerMax => Unit->AtkValues[50].Int;
        public int MeticulousPower => Unit->AtkValues[51].Int;
    }

    private static int ParseFirstInt(string text) {
        var match = Digits().Match(text);
        return match.Success ? int.Parse(match.Value) : 0;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
