using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Text.RegularExpressions;
using visland.Helpers;

namespace visland.Gathering.AutoGather;

public static unsafe partial class GatheringAddon {
    public sealed class Gathering {
        private const string AddonName = "Gathering";

        /// <summary>
        /// 當下這一刻的 addon 指標，查不到回 <c>null</c>。
        /// <para>🔴 <b>刻意不快取、不跨幀保存。</b> 原本的寫法是在 <c>PostSetup</c> 把
        /// <c>args.Addon</c> 存進 <c>readonly</c> 欄位、靠 <c>PreFinalize</c> 清成 null。
        /// 那條生命週期只要沒走到（addon 開著時外掛 reload、事件沒送達……），
        /// 欄位裡就是**懸空指標**——而懸空**不等於 null**，所有既有的判空一個都擋不住，
        /// 解參考下去就是存取違規，且 AVE 是 .NET Core 的 corrupted-state exception，
        /// 外圈 <c>try/catch</c> 完全攔不到。</para>
        /// <para>📌 重查的代價：一次原生 <c>GetAddonByName</c>（unit list 名稱比對），
        /// 字串參數走 <c>stackalloc</c> 不配置堆積，對每幀路徑而言可以忽略。
        /// 🔴 但呼叫端一律「<b>取一次存成區域變數</b>」再用，不要在同一條 <c>-&gt;</c> 鏈裡重查——
        /// 那既多花錢又會製造 TOCTOU。</para>
        /// <para>🔴 查不到是常態（addon 沒開），**不記錄**——這是每幀路徑。</para>
        /// </summary>
        private static AddonGathering* Addon => (AddonGathering*)Service.GameGui.GetAddonByName(AddonName).Address;

        /// <summary>
        /// 三態版本的目前完整度：<c>null</c> ＝ <b>現在讀不到</b>（addon 或文字節點還沒建好／正在收），
        /// <b>不代表</b>「完整度歸零」。要拿它來比大小的地方一律用這個版本。
        /// </summary>
        public int? CurrentIntegrityOrUnknown => IntegrityOf(9);

        /// <summary>三態版本的完整度上限，語意同 <see cref="CurrentIntegrityOrUnknown"/>。</summary>
        public int? TotalIntegrityOrUnknown => IntegrityOf(12);

        /// <summary>
        /// 讀不到時回 <c>0</c>。這個預設值是刻意挑的：唯一的每幀進入點
        /// <c>GatherRouteExec.Update</c> 判的是 <c>CurrentIntegrity: &gt; 0</c>，
        /// 回 0 會讓那一幀整段自動採集**不進場**（no-op、下一幀重試），
        /// 而不是誤觸任何動作。⚠️ 但「0 ＝ 讀不到」和「0 ＝ 真的採完了」在這個型別上分不開，
        /// 所以任何**比大小**的判斷（例如 <c>Current &lt; Total</c> 決定要不要放回復完整度）
        /// 必須改用上面的三態版本，否則「讀不到」會被當成「完整度掉了」而白放技能。
        /// </summary>
        public int CurrentIntegrity => CurrentIntegrityOrUnknown ?? 0;

        /// <summary>讀不到時回 <c>0</c>，語意與注意事項同 <see cref="CurrentIntegrity"/>。</summary>
        public int TotalIntegrity => TotalIntegrityOrUnknown ?? 0;

        /// <summary>
        /// 讀 addon 上某個文字節點裡的第一個整數。
        /// <para>🔴 <c>GetTextNodeById</c> 是 <c>[MemberFunction]</c> 原生呼叫：對 null addon 呼叫即存取違規；
        /// 而且它在節點還沒建好時**會回 null**，接著解 <c>-&gt;NodeText</c> 是第二個入口。
        /// AVE 是 .NET Core 的 corrupted-state exception，外圈 <c>try/catch</c> 完全攔不到。</para>
        /// <para>🔴 這是每幀路徑（<c>GatherRouteExec.Update</c>），失敗只回 <c>null</c>，**不記錄**。</para>
        /// </summary>
        private static int? IntegrityOf(uint nodeId) {
            var addon = Addon; // 取一次，下面整條鏈都用這個當幀值
            if (addon == null) return null;
            var node = addon->GetTextNodeById(nodeId);
            return node == null ? null : ParseFirstInt(node->NodeText.ToString());
        }

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
            // 整個操作只解析一次 addon：核取方塊與 FireCallback 必須指向同一幀的同一個 addon，
            // 中間重查等於允許兩者來自不同物件。
            var addon = Addon;
            if (addon == null)
                return;
            // CheckBoxEnabled 只有在「已確認可勾選」時才回 true；讀不到（元件或 OwnerNode 還沒建好）
            // 回 null，一樣不送 callback——失敗形式是「這一幀不做事」，下一幀重試。
            if (CheckBoxEnabled(CheckboxAt(addon, index)) != true)
                return;
            // 🔴 採集視窗按一次採一格、窗留著(多次互動窗,逃生口 15 幀),但「最後一格採完後
            //    遊戲自己收窗」的淡出期間,上面那些文字節點與核取方塊仍讀得到值
            //    ——「三關全過」擋不住關閉中的窗,此時再送 callback 就是攔不到的原生存取違規。
            //    守衛記的是「這個位址的這一格按過了」,所以 Framework.Update／IPC／偵錯按鈕
            //    三條驅動都走同一個閘門(它們全都經過這裡)。
            if (!AddonPressGuard.TryBeginPress(AddonName, (nint)addon, GatherPressKey(index)))
                return;
            var values = stackalloc AtkValue[2];
            values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            values[0].Int = 2;
            values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
            values[1].UInt = (uint)index;
            ((AtkUnitBase*)addon)->FireCallback(1, values);
        }

        /// <summary>
        /// 每一格的守衛按法名稱。預先建好字串:<c>Gather</c> 在每幀路徑上,不要在那裡臨時組字串。
        /// 索引超出範圍時退回共用名稱(那次會與別格互擋,偏保守)。
        /// </summary>
        private static readonly string[] GatherPressKeys =
            ["Gather0", "Gather1", "Gather2", "Gather3", "Gather4", "Gather5", "Gather6", "Gather7"];

        private static string GatherPressKey(int index)
            => (uint)index < (uint)GatherPressKeys.Length ? GatherPressKeys[index] : "Gather";

        // addon 為 null 時不解參考（FixedSizeArray 的索引器本身就是 this 上的偏移計算）。
        // 🔴 addon 由呼叫端解析後傳進來，不在這裡重查：同一次操作內必須是同一個 addon。
        private static AtkComponentCheckBox* CheckboxAt(AddonGathering* addon, int index) => addon == null ? null : index switch {
            0 => addon->GatheredItemComponentCheckbox[0],
            1 => addon->GatheredItemComponentCheckbox[1],
            2 => addon->GatheredItemComponentCheckbox[2],
            3 => addon->GatheredItemComponentCheckbox[3],
            4 => addon->GatheredItemComponentCheckbox[4],
            5 => addon->GatheredItemComponentCheckbox[5],
            6 => addon->GatheredItemComponentCheckbox[6],
            7 => addon->GatheredItemComponentCheckbox[7],
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
            private AtkComponentCheckBox* CheckBox => CheckboxAt(Addon, index);

            /// <summary>三態版本：<c>null</c> ＝ 現在讀不到，<b>不是</b>「已確認停用」。</summary>
            public bool? IsEnabledOrUnknown => CheckBoxEnabled(CheckBox);

            /// <summary>只有「已確認可勾選」才回 <c>true</c>；讀不到一律 <c>false</c>（＝這次不動作）。</summary>
            public bool IsEnabled => IsEnabledOrUnknown == true;

            public string ItemName => TextOf(23);
            // ItemIds 是 FixedSizeArray8（index 固定 0..7，上界由呼叫端保證），
            // 但 addon 本身仍要判空，否則索引器等於在 null 上算偏移再解參考。
            public uint ItemID {
                get {
                    var addon = Addon;
                    return addon == null ? 0 : addon->ItemIds[index];
                }
            }
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
        private const string AddonName = "GatheringMasterpiece";

        /// <summary>
        /// 當下這一刻的 addon 指標，查不到回 <c>null</c>。
        /// 🔴 刻意不快取、不跨幀保存，理由與 <see cref="Gathering"/> 的同名成員相同：
        /// 靠 <c>PreFinalize</c> 清指標的舊寫法只要事件沒送達就留下**懸空指標**，
        /// 而懸空**不等於 null**，判空擋不住、<c>try/catch</c> 也攔不到 AVE。
        /// </summary>
        private static AddonGatheringMasterpiece* Addon => (AddonGatheringMasterpiece*)Service.GameGui.GetAddonByName(AddonName).Address;

        private static AtkUnitBase* Unit => (AtkUnitBase*)Addon;

        /// <summary>
        /// 取第 <paramref name="index"/> 個 <c>AtkValue</c>，取不到回 <c>null</c>。
        /// <para>🔴 <c>AtkUnitBase.AtkValues</c> 是**原生指標陣列，C# 的 <c>Length</c> 幫不上忙**——
        /// 只判空是半套：索引 62／63 在 addon 剛 setup、值還沒填滿時是**讀陣列後方的堆積垃圾**，
        /// 讀到的不是 null 而是隨機數字，會被下游當成真的完整度／收藏價值拿去比大小並放技能。
        /// 上界的權威來源是同結構 <c>+0x1E2</c> 的 <c>AtkValuesCount</c>。</para>
        /// <para>📌 **刻意不驗 <c>Type</c>**：型別不符只會讀到同一個 union 內的別的欄位（不會越界、
        /// 不會 AVE），而台服實際塞什麼 <c>ValueType</c> 無法離線確認——加嚴會靜默停掉本來正常的採集。
        /// 這次只補「不越界」，不動數值語意。</para>
        /// </summary>
        private static AtkValue* ValueAt(int index) {
            var unit = Unit; // 取一次，下面整段都用這個當幀值
            if (unit == null) return null;
            var values = unit->AtkValues;
            if (values == null || index < 0 || index >= unit->AtkValuesCount) return null;
            return &values[index];
        }

        private static int? IntAt(int index) {
            var v = ValueAt(index);
            return v == null ? null : v->Int;
        }

        private static uint? UIntAt(int index) {
            var v = ValueAt(index);
            return v == null ? null : v->UInt;
        }

        /// <summary>
        /// 收藏品採集的道具名稱。<c>ItemName</c> 是 addon 裡的 <c>AtkTextNode*</c>，
        /// setup 當幀／收視窗時可能還是 null，直接解 <c>-&gt;NodeText</c> 就是存取違規入口。
        /// 讀不到回空字串。
        /// </summary>
        public string ItemName {
            get {
                var addon = Addon; // 取一次：判空與解參考必須是同一個物件
                return addon == null || addon->ItemName == null
                    ? string.Empty
                    : addon->ItemName->NodeText.ToString();
            }
        }

        /// <summary>三態版本：<c>null</c> ＝ 現在讀不到（陣列還沒配好／長度不足），<b>不是</b>「值就是 0」。</summary>
        public int? CurrentCollectabilityOrUnknown => IntAt(13);
        /// <inheritdoc cref="CurrentCollectabilityOrUnknown"/>
        public int? MaxCollectabilityOrUnknown => IntAt(14);
        /// <inheritdoc cref="CurrentCollectabilityOrUnknown"/>
        public uint? CurrentIntegrityOrUnknown => UIntAt(62);
        /// <inheritdoc cref="CurrentCollectabilityOrUnknown"/>
        public uint? TotalIntegrityOrUnknown => UIntAt(63);
        /// <inheritdoc cref="CurrentCollectabilityOrUnknown"/>
        public int? ScourPowerOrUnknown => IntAt(48);
        /// <inheritdoc cref="CurrentCollectabilityOrUnknown"/>
        public int? MeticulousPowerOrUnknown => IntAt(51);

        public uint ItemID => UIntAt(2) ?? 0;
        public int CurrentCollectability => CurrentCollectabilityOrUnknown ?? 0;
        public int MaxCollectability => MaxCollectabilityOrUnknown ?? 0;

        /// <summary>
        /// 讀不到時回 <c>0</c>：每幀進入點 <c>GatherRouteExec.Update</c> 判的是
        /// <c>CurrentIntegrity: &gt; 0</c>，回 0 讓那一幀整段不進場（no-op、下一幀重試）。
        /// ⚠️ 需要比大小或做算術的地方請用 <see cref="CurrentIntegrityOrUnknown"/>。
        /// </summary>
        public uint CurrentIntegrity => CurrentIntegrityOrUnknown ?? 0;
        /// <inheritdoc cref="CurrentIntegrity"/>
        public uint TotalIntegrity => TotalIntegrityOrUnknown ?? 0;
        public int ScourPower => ScourPowerOrUnknown ?? 0;
        public int BrazenPowerMin => IntAt(49) ?? 0;
        public int BrazenPowerMax => IntAt(50) ?? 0;
        public int MeticulousPower => MeticulousPowerOrUnknown ?? 0;
    }

    private static int ParseFirstInt(string text) {
        var match = Digits().Match(text);
        return match.Success ? int.Parse(match.Value) : 0;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
