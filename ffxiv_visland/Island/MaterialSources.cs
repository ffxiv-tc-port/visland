using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using visland.Helpers;

namespace visland.Island;

// 無人島材料的「來源」查表。**純 Excel 資料層,不讀任何遊戲記憶體**,所以可以在建構時做一次就快取。
//
// 🔴 MJIItemPouch 第 0 列是真材料(無人島棕櫚葉)不是「無」。
//    任何 `pouchId == 0 -> continue` 都會靜默漏掉棕櫚葉 —— 有效性一律用「數量 > 0」判斷。
// 🔴 MJIRecipe.Material[] 指向的是 **MJIRecipeMaterial** 不是 MJIItemPouch(多一層轉接);
//    直接當成 MJIItemPouch 讀會得到一份看起來合理但完全錯誤的配方表。
//    (校驗:MJIRecipe 0 = 開拓用石斧,經轉接後得到棕櫚葉x3 + 小樹枝x2 + 石材x2,與遊戲一致。)
// 🔴 MJIRecipe 有 19 列 ItemPouch = 0,那是**工具配方**(產物在 KeyItem 欄),
//    照 ItemPouch 讀會把它們全部算成「產出棕櫚葉」。
// ⚠️ MJIStockyardManagementArea.Area 的社群 schema 標的是 MJIText,但 MJIText 1~6 是
//    「空手/小島木屋 I~III/開拓工坊 I~II」,對不上;實際內容在 **MJIName** 1~6
//    (草原/溪流/森林/沙灘/山/洞窟),且與各遠征地的材料清單完全吻合(洞窟那列剛好是石炭/
//    堆積岩/燈火茸/氣泡水/幻影石/黃銅礦/金礦/鷹眼砂/水晶層)。所以這裡用 RowId 去查 MJIName。
public sealed class MaterialGatherInfo {
    public uint GatheringItemRow;
    public float X;
    public float Z;
    public float Radius;
    public uint ToolKeyItemRow; // MJIKeyItem 列號,0 = 徒手
    public string ToolName = "";
    public bool Cave; // MJIGatheringItem.Unknown1 == 1;實測與「洞窟遠征地」的 9 種材料完全一致
}

public sealed class MaterialCraftUse {
    public uint CraftObjectRow;
    public string CraftName = "";
    public int Amount;
}

public sealed class MaterialRecipeUse {
    public uint RecipeRow;
    public string ProductName = "";
    public int Amount;
    public bool Wildcard; // 配方吃的是「同類任選」而不是這一項材料
}

public sealed class MaterialInfo {
    public uint PouchId;
    public uint ItemId;
    public string Name = "";
    public uint CategoryId;
    public string CategoryName = "";

    public MaterialGatherInfo? Gather;
    public List<uint> ExpeditionAreas = [];     // MJIStockyardManagementArea 列號(一般材料)
    public List<uint> RareExpeditionAreas = []; // 同上,但這裡是該遠征地的稀有材料
    public uint CropSeedRow;                    // != 0 => 這是農園收成物(MJICropSeed 列號)
    public uint SeedOfCropRow;                  // != 0 => 這是種子/芽塊
    public bool FromPasture;                    // 牧場動物的產物
    public bool Handicraft;                     // 無人島製作(MJIRecipe)做得出來

    public List<MaterialCraftUse> UsedByCrafts = [];   // 開拓工坊配方(需求陣列統計的就是這些)
    public List<MaterialRecipeUse> UsedByRecipes = []; // 無人島製作配方(不列入工坊需求)

    public bool HasKnownSource => Gather != null || ExpeditionAreas.Count > 0 || RareExpeditionAreas.Count > 0
        || CropSeedRow != 0 || SeedOfCropRow != 0 || FromPasture || Handicraft;
}

public static class MaterialSources {
    // CS 的 AgentMJICraftSchedule.MaterialAllocationEntry.UsedAmounts 是 FixedSizeArray109,
    // IslandState.LockedPouchItems 也是 109。表加列時**這個常數不會跟著長**,
    // 所以所有索引都要對「陣列長度」與「表列數」取小的那個。
    public const int PouchArrayLength = 109;

    private static MaterialInfo[]? _all;
    private static Dictionary<uint, uint>? _pouchByItemId;
    private static bool _failed;

    // 索引 == MJIItemPouch 列號。長度 = min(109, 表列數),對兩邊都不會越界。
    public static MaterialInfo[] All {
        get {
            if (_all == null && !_failed)
                Build();
            return _all ?? [];
        }
    }

    public static MaterialInfo? ByPouchId(uint pouchId) {
        var all = All;
        return pouchId < all.Length ? all[pouchId] : null;
    }

    public static bool TryGetPouchIdByItemId(uint itemId, out uint pouchId) {
        _ = All;
        pouchId = 0;
        return _pouchByItemId != null && _pouchByItemId.TryGetValue(itemId, out pouchId);
    }

    private static void Build() {
        try {
            var pouchSheet = MJIItemPouch.Get();
            var count = Math.Min(PouchArrayLength, pouchSheet.Count);
            var all = new MaterialInfo[count];
            var byItem = new Dictionary<uint, uint>();

            for (var i = 0u; i < count; ++i) {
                var row = MJIItemPouch.GetRow(i);
                var info = new MaterialInfo { PouchId = i };
                if (row != null) {
                    info.ItemId = row.Value.Item.RowId;
                    info.Name = row.Value.Item.Value.Name.ToString();
                    info.CategoryId = row.Value.Category.RowId;
                    info.CategoryName = row.Value.Category.Value.Singular.ToString();
                    info.SeedOfCropRow = row.Value.Crop.RowId;
                    if (info.ItemId != 0)
                        byItem[info.ItemId] = i;
                }
                if (info.Name.Length == 0)
                    info.Name = $"#{i}";
                all[i] = info;
            }

            AddGathering(all, byItem);
            AddExpeditions(all);
            AddFarm(all, byItem);
            AddPasture(all, byItem);
            AddCraftUses(all);
            AddRecipes(all);

            _pouchByItemId = byItem;
            _all = all;
            Service.Log.Information($"[Materials] built source table for {all.Length} pouch rows");
        }
        catch (Exception ex) {
            _failed = true;
            Service.Log.Error(ex, "[Materials] failed to build source table from sheets");
        }
    }

    private static void AddGathering(MaterialInfo[] all, Dictionary<uint, uint> byItem) {
        foreach (var gi in MJIGatheringItem.Get()) {
            var itemId = gi.Item.RowId;
            if (itemId == 0)
                continue; // 第 0 列是全零佔位,不是材料
            if (!byItem.TryGetValue(itemId, out var pouchId) || pouchId >= all.Length)
                continue;

            var toolRow = MJIGatheringTool.GetRow(gi.Unknown0);
            var keyItemRow = toolRow != null ? (uint)toolRow.Value.Unknown0 : 0u;
            var toolName = "";
            if (keyItemRow != 0) {
                var keyItem = MJIKeyItem.GetRow(keyItemRow);
                if (keyItem != null)
                    toolName = keyItem.Value.Item.Value.Name.ToString();
            }

            all[pouchId].Gather = new MaterialGatherInfo {
                GatheringItemRow = gi.RowId,
                X = gi.X,
                Z = gi.Y, // 🔴 表上叫 Y,但實際是世界座標的 Z(用 23 條實機路線的 394 個路徑點離線驗過)
                Radius = gi.Radius,
                ToolKeyItemRow = keyItemRow,
                ToolName = toolName,
                Cave = gi.Unknown1 != 0,
            };
        }
    }

    private static void AddExpeditions(MaterialInfo[] all) {
        foreach (var area in MJIStockyardManagementArea.Get()) {
            var rare = area.RareMaterial.RowId;
            if (rare < all.Length)
                all[rare].RareExpeditionAreas.Add(area.RowId);
        }

        var table = Service.DataManager.GetSubrowExcelSheet<MJIStockyardManagementTable>();
        foreach (var group in table) {
            foreach (var sub in group) {
                var pouchId = sub.Material.RowId;
                if (pouchId < all.Length && !all[pouchId].ExpeditionAreas.Contains(group.RowId))
                    all[pouchId].ExpeditionAreas.Add(group.RowId);
            }
        }
    }

    private static void AddFarm(MaterialInfo[] all, Dictionary<uint, uint> byItem) {
        foreach (var seed in MJICropSeed.Get()) {
            var itemId = seed.Item.RowId;
            if (itemId == 0)
                continue;
            if (byItem.TryGetValue(itemId, out var pouchId) && pouchId < all.Length)
                all[pouchId].CropSeedRow = seed.RowId;
        }
    }

    private static void AddPasture(MaterialInfo[] all, Dictionary<uint, uint> byItem) {
        foreach (var animal in MJIAnimals.Get()) {
            for (var i = 0; i < animal.Reward.Count; ++i) {
                var itemId = animal.Reward[i].RowId;
                if (itemId == 0)
                    continue;
                if (byItem.TryGetValue(itemId, out var pouchId) && pouchId < all.Length)
                    all[pouchId].FromPasture = true;
            }
        }
    }

    private static void AddCraftUses(MaterialInfo[] all) {
        foreach (var craft in MJICraftworksObject.Get()) {
            if (craft.Item.RowId == 0)
                continue; // 第 0 列與台服 7.20 的 85~90 列都是全零佔位
            var name = craft.Item.Value.Name.ToString();
            for (var i = 0; i < craft.Material.Count && i < craft.Amount.Count; ++i) {
                var amount = craft.Amount[i];
                if (amount <= 0)
                    continue; // 🔴 判「有沒有這個材料」要看 Amount,不能看 Material != 0
                var pouchId = craft.Material[i].RowId;
                if (pouchId >= all.Length)
                    continue;
                all[pouchId].UsedByCrafts.Add(new MaterialCraftUse {
                    CraftObjectRow = craft.RowId,
                    CraftName = name,
                    Amount = amount,
                });
            }
        }
    }

    private static void AddRecipes(MaterialInfo[] all) {
        foreach (var recipe in MJIRecipe.Get()) {
            var isKeyItemRecipe = recipe.KeyItem.RowId != 0;
            var productName = "";
            if (isKeyItemRecipe) {
                var keyItem = MJIKeyItem.GetRow(recipe.KeyItem.RowId);
                if (keyItem != null)
                    productName = keyItem.Value.Item.Value.Name.ToString();
            }
            else {
                var product = recipe.ItemPouch.RowId;
                if (product < all.Length) {
                    all[product].Handicraft = true;
                    productName = all[product].Name;
                }
            }
            if (productName.Length == 0)
                continue;

            for (var i = 0; i < recipe.Material.Count && i < recipe.Amount.Count; ++i) {
                var amount = recipe.Amount[i];
                if (amount <= 0)
                    continue;
                var mat = MJIRecipeMaterial.GetRow(recipe.Material[i].RowId);
                if (mat == null)
                    continue;

                // MJIRecipeMaterial.Unknown0 != 0 的那些不是具體材料,而是「同類任選」
                // (台服 7.20 只有 1 列:ItemPouch=0 / Unknown0=4,而 MJIItemCategory 4 = 開拓作物,
                //  對應蔬菜飼料/高級蔬菜飼料吃任意作物)。照 ItemPouch 讀會全部算到棕櫚葉頭上。
                if (mat.Value.Unknown0 != 0) {
                    var category = (uint)mat.Value.Unknown0;
                    foreach (var info in all) {
                        if (info.CategoryId != category)
                            continue;
                        info.UsedByRecipes.Add(new MaterialRecipeUse {
                            RecipeRow = recipe.RowId,
                            ProductName = productName,
                            Amount = amount,
                            Wildcard = true,
                        });
                    }
                    continue;
                }

                var pouchId = mat.Value.ItemPouch.RowId;
                if (pouchId >= all.Length)
                    continue;
                all[pouchId].UsedByRecipes.Add(new MaterialRecipeUse {
                    RecipeRow = recipe.RowId,
                    ProductName = productName,
                    Amount = amount,
                });
            }
        }
    }

    public static string ExpeditionAreaName(uint areaRow) {
        var area = MJIStockyardManagementArea.GetRow(areaRow);
        if (area == null)
            return $"#{areaRow}";
        // 見檔頭:Area 欄實際指向 MJIName,社群 schema 標成 MJIText 是錯的。
        var name = MJIName.GetRow(area.Value.Area.RowId);
        var text = name?.Singular.ToString() ?? "";
        return text.Length > 0 ? text : $"#{areaRow}";
    }
}
