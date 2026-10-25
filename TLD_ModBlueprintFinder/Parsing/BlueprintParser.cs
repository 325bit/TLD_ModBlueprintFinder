using System.Text;
using System.Text.Json;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>
/// 把 modcomponent 里的一条 JSON 解析成 <see cref="BlueprintRecord"/>。
///
/// ── 数据来源与字段含义（来自对实机 Mods 目录 45 个 modcomponent / 416 份蓝图 JSON 的实测）──
///
/// 【两种文件布局】
///   1. blueprints/&lt;名字&gt;.json  → 顶层对象就是蓝图本身
///   2. recipes/&lt;名字&gt;.json     → 顶层是食谱包装对象，蓝图嵌在 "BlueprintData" 里，
///                                 外层还有 RecipeName / RecipeDescription / AllowedCookingPots /
///                                 RequiredSkillLevel 等烹饪专属字段
///   两种都要解析，否则只做了 blueprints/ 的工具会漏掉全部「食谱」类配方。
///
/// 【核心字段】
///   Name                → 蓝图 ID（游戏内唯一键，也是去重键）
///   CraftedResult       → 产物物品 ID  ← 「作为产物」检索的匹配字段
///   CraftedResultCount  → 产物数量
///   RequiredGear[]      → 原料列表    ← 「作为原料」检索的匹配字段
///       Item            → 物品 ID
///       Count           → 件数（805 次）
///       Quantity+Units  → 按重量/体积计（Kilograms 64 次、Count 105 次），与 Count 互斥
///   RequiredPowder[]    → Powder + QuantityInKilograms
///   RequiredLiquid[]    → Liquid + VolumeInLitres
///   RequiredTool        → 单个字符串（空串表示没有，绝不能被当成物品 ID "        " 去索引）
///   OptionalTools[]     → 可选工具列表
///   RequiredCraftingLocation → Anywhere / Workbench / Fire / Forge / AmmoWorkbench / FurnitureWorkbench
///   GunpowderRequired   → 旧字段名，8 处，新版叫 GunpowderKGRequired
/// </summary>
public static class BlueprintParser
{
    /// <summary>单条 JSON 的解析结果。</summary>
    public readonly record struct ParseOutcome(BlueprintRecord? Record, string? SkipReason);

    /// <summary>
    /// 解析一条条目。<paramref name="rawJson"/> 为 null 表示读取失败，交由调用方记错误。
    /// </summary>
    public static ParseOutcome Parse(
        string modName,
        string containerPath,
        string entryPath,
        BlueprintSourceKind sourceKindHint,
        byte[] jsonBytes)
    {
        string text;
        try
        {
            text = DecodeText(jsonBytes);
        }
        catch (Exception ex)
        {
            return new ParseOutcome(null, $"文本解码失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
            return new ParseOutcome(null, "文件为空");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
        }
        catch (JsonException ex)
        {
            return new ParseOutcome(null, $"JSON 语法错误：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // 顶层可能是数组（少数打包器会把多条蓝图塞进一个文件）
            if (root.ValueKind == JsonValueKind.Array)
            {
                // 这里只返回第一条；调用方应改用 ParseMany。
                var first = root.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                    return new ParseOutcome(null, "顶层数组内没有对象");
                return Build(modName, containerPath, entryPath, sourceKindHint, first, text);
            }

            if (root.ValueKind != JsonValueKind.Object)
                return new ParseOutcome(null, $"顶层不是对象（{root.ValueKind}）");

            return Build(modName, containerPath, entryPath, sourceKindHint, root, text);
        }
    }

    /// <summary>一个文件可能装了多条蓝图，这里统一展开。</summary>
    public static IReadOnlyList<BlueprintRecord> ParseMany(
        string modName,
        string containerPath,
        string entryPath,
        BlueprintSourceKind sourceKindHint,
        byte[] jsonBytes,
        List<string> errors)
    {
        var text = DecodeText(jsonBytes);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<BlueprintRecord>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
        }
        catch (JsonException ex)
        {
            errors.Add($"{modName} :: {entryPath} → JSON 语法错误：{ex.Message}");
            return Array.Empty<BlueprintRecord>();
        }

        using (doc)
        {
            var root = doc.RootElement;
            var output = new List<BlueprintRecord>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { index++; continue; }
                    var outcome = Build(modName, containerPath, $"{entryPath}[{index}]", sourceKindHint, item, text);
                    if (outcome.Record is not null) output.Add(outcome.Record);
                    else if (outcome.SkipReason is not null)
                        errors.Add($"{modName} :: {entryPath}[{index}] → {outcome.SkipReason}");
                    index++;
                }

                return output;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{modName} :: {entryPath} → 顶层不是对象（{root.ValueKind}）");
                return Array.Empty<BlueprintRecord>();
            }

            var single = Build(modName, containerPath, entryPath, sourceKindHint, root, text);
            if (single.Record is not null) output.Add(single.Record);
            else if (single.SkipReason is not null)
                errors.Add($"{modName} :: {entryPath} → {single.SkipReason}");

            return output;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 核心映射
    // ────────────────────────────────────────────────────────────────────

    private static ParseOutcome Build(
        string modName,
        string containerPath,
        string entryPath,
        BlueprintSourceKind sourceKindHint,
        JsonElement root,
        string rawText)
    {
        // ① 先判断是不是「食谱包装」：recipes/ 下的文件把蓝图塞进 BlueprintData
        JsonElement blueprintObj;
        string? recipeName = null;
        string? recipeDesc = null;
        var sourceKind = sourceKindHint;
        var recipeExtraTools = new List<Ingredient>();

        if (JsonHelpers.TryGet(root, out var bpData, "BlueprintData", "Blueprint") &&
            bpData.ValueKind == JsonValueKind.Object)
        {
            blueprintObj = bpData;
            sourceKind = BlueprintSourceKind.Recipe;
            recipeName = JsonHelpers.GetString(root, "RecipeName", "NameID", "LocalizationKey");
            recipeDesc = JsonHelpers.GetString(root, "RecipeDescription", "DescriptionID");

            // AllowedCookingPots 属于食谱层，但它是「以某物品为工具」的真实引用
            foreach (var pot in JsonHelpers.GetArrayLike(root, "AllowedCookingPots", "AllowedCookingPotsList"))
            {
                var id = JsonHelpers.AsString(pot);
                if (!string.IsNullOrWhiteSpace(id))
                    recipeExtraTools.Add(new Ingredient
                    {
                        ItemId = id,
                        Kind = IngredientKind.Tool,
                        Role = ToolRole.CookingPot
                    });
            }
        }
        else
        {
            blueprintObj = root;
        }

        var warnings = new List<string>();

        // ② 蓝图 ID
        var name = JsonHelpers.GetString(blueprintObj, "Name", "BlueprintName", "ID", "Id");
        if (string.IsNullOrWhiteSpace(name))
        {
            // 退化：用文件名兜底，但记录警告，方便用户知道这条不是规范数据
            name = Path.GetFileNameWithoutExtension(entryPath);
            warnings.Add("缺少 Name 字段，已用文件名兜底");
        }

        // ③ 产物
        var crafted = JsonHelpers.GetString(blueprintObj,
            "CraftedResult", "Result", "CraftedResultGearName", "ResultItem", "Output", "OutputItem");
        if (string.IsNullOrWhiteSpace(crafted))
        {
            warnings.Add("缺少 CraftedResult 字段：无法参与「作为产物」检索");
            crafted = string.Empty;
        }

        var craftedCount = JsonHelpers.GetDouble(blueprintObj,
            "CraftedResultCount", "ResultCount", "OutputCount") ?? 1;

        // ④ 原料
        var ingredients = new List<Ingredient>();
        ingredients.AddRange(ParseGearRequirements(blueprintObj, warnings));
        ingredients.AddRange(ParsePowderRequirements(blueprintObj, warnings));
        ingredients.AddRange(ParseLiquidRequirements(blueprintObj, warnings));

        // ⑤ 工具
        var tools = new List<Ingredient>();
        tools.AddRange(ParseTools(blueprintObj, warnings));
        tools.AddRange(recipeExtraTools);

        // ⑥ 额外条件（不能当原料索引，但玩家查配方时很关键）
        var extras = new List<string>();
        var kerosene = JsonHelpers.GetDouble(blueprintObj, "KeroseneLitersRequired", "KeroseneRequired");
        if (kerosene is > 0) extras.Add($"煤油 {kerosene.Value:0.###} L");

        var gunpowder = JsonHelpers.GetDouble(blueprintObj, "GunpowderKGRequired", "GunpowderRequired");
        if (gunpowder is > 0) extras.Add($"火药 {gunpowder.Value:0.###} kg");

        if (JsonHelpers.GetBool(blueprintObj, "RequiresLitFire") == true) extras.Add("需要点燃的火");
        if (JsonHelpers.GetBool(blueprintObj, "RequiresLight") == true) extras.Add("需要照明");

        var skill = JsonHelpers.GetString(blueprintObj, "AppliedSkill");
        if (!string.IsNullOrWhiteSpace(skill) && !skill.Equals("None", StringComparison.OrdinalIgnoreCase))
            extras.Add($"技能 {skill}");

        var improved = JsonHelpers.GetString(blueprintObj, "ImprovedSkill");
        if (!string.IsNullOrWhiteSpace(improved) && !improved.Equals("None", StringComparison.OrdinalIgnoreCase))
            extras.Add($"提升技能 {improved}");

        var skillLevel = JsonHelpers.GetDouble(root, "RequiredSkillLevel");
        if (skillLevel is > 0) extras.Add($"烹饪等级 {skillLevel.Value:0}");

        var record = new BlueprintRecord
        {
            ModName = modName,
            ContainerPath = containerPath,
            EntryPath = entryPath,
            SourceKind = sourceKind,
            Name = name!,
            RecipeName = recipeName,
            RecipeDescription = recipeDesc,
            CraftedResult = crafted!,
            CraftedResultCount = craftedCount,
            RawCraftingLocation = JsonHelpers.GetString(blueprintObj, "RequiredCraftingLocation", "CraftingLocation"),
            DurationMinutes = JsonHelpers.GetDouble(blueprintObj, "DurationMinutes", "Duration") ?? 0,
            Ingredients = ingredients,
            Tools = tools,
            ExtraRequirements = extras,
            Warnings = warnings,
            RawJson = rawText
        };

        return new ParseOutcome(record, null);
    }

    /// <summary>
    /// RequiredGear：三种写法都要认。
    ///   "GEAR_X"                                          → 件数 1
    ///   { "Item": "GEAR_X", "Count": 3 }                   → 件数 3
    ///   { "Item": "GEAR_X", "Quantity": 0.1, "Units": "Kilograms" } → 0.1 kg
    /// </summary>
    private static IEnumerable<Ingredient> ParseGearRequirements(JsonElement obj, List<string> warnings)
    {
        foreach (var element in JsonHelpers.GetArrayLike(obj, "RequiredGear", "RequiredItems", "Ingredients", "Materials"))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var s = JsonHelpers.AsString(element);
                if (!string.IsNullOrWhiteSpace(s))
                    yield return new Ingredient { ItemId = s, Kind = IngredientKind.Gear, Count = 1 };
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"RequiredGear 中出现无法识别的元素（{element.ValueKind}）");
                continue;
            }

            var itemId = JsonHelpers.GetString(element, "Item", "GearItem", "ItemName", "Gear", "Name", "ID");
            if (string.IsNullOrWhiteSpace(itemId))
            {
                warnings.Add("RequiredGear 元素缺少 Item 字段");
                continue;
            }

            var count = JsonHelpers.GetDouble(element, "Count", "Amount", "Number", "ItemCount");
            var quantity = JsonHelpers.GetDouble(element, "Quantity", "Weight", "Mass", "Volume");
            var units = JsonHelpers.GetString(element, "Units", "Unit", "Measurement");

            // Units=Count 时 Quantity 其实表示件数，归一化成 Count
            if (quantity.HasValue && units is not null &&
                (units.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                 units.Equals("Units", StringComparison.OrdinalIgnoreCase)))
            {
                count ??= quantity;
                quantity = null;
                units = null;
            }

            if (!count.HasValue && !quantity.HasValue)
            {
                // 两者都没有：按 1 件处理，但要留痕
                warnings.Add($"原料 {itemId} 未声明数量，按 1 件处理");
                count = 1;
            }

            yield return new Ingredient
            {
                ItemId = itemId,
                Kind = IngredientKind.Gear,
                Count = count,
                Quantity = quantity,
                Units = units
            };
        }
    }

    /// <summary>RequiredPowder：[{ "Powder": "POWDER_Salt", "QuantityInKilograms": 0.01 }]</summary>
    private static IEnumerable<Ingredient> ParsePowderRequirements(JsonElement obj, List<string> warnings)
    {
        foreach (var element in JsonHelpers.GetArrayLike(obj, "RequiredPowder", "RequiredPowders"))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var s = JsonHelpers.AsString(element);
                if (!string.IsNullOrWhiteSpace(s))
                    yield return new Ingredient { ItemId = s, Kind = IngredientKind.Powder, Quantity = 0, Units = "Kilograms" };
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"RequiredPowder 中出现无法识别的元素（{element.ValueKind}）");
                continue;
            }

            var id = JsonHelpers.GetString(element, "Powder", "PowderType", "Item", "Name", "Type");
            if (string.IsNullOrWhiteSpace(id))
            {
                warnings.Add("RequiredPowder 元素缺少 Powder 字段");
                continue;
            }

            var kg = JsonHelpers.GetDouble(element, "QuantityInKilograms", "Quantity", "Kilograms", "Amount") ?? 0;
            yield return new Ingredient { ItemId = id, Kind = IngredientKind.Powder, Quantity = kg, Units = "Kilograms" };
        }
    }

    /// <summary>RequiredLiquid：[{ "Liquid": "LIQUID_WaterPotable", "VolumeInLitres": 1.5 }]</summary>
    private static IEnumerable<Ingredient> ParseLiquidRequirements(JsonElement obj, List<string> warnings)
    {
        foreach (var element in JsonHelpers.GetArrayLike(obj, "RequiredLiquid", "RequiredLiquids"))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var s = JsonHelpers.AsString(element);
                if (!string.IsNullOrWhiteSpace(s))
                    yield return new Ingredient { ItemId = s, Kind = IngredientKind.Liquid, Quantity = 0, Units = "Litres" };
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"RequiredLiquid 中出现无法识别的元素（{element.ValueKind}）");
                continue;
            }

            var id = JsonHelpers.GetString(element, "Liquid", "LiquidType", "Item", "Name", "Type");
            if (string.IsNullOrWhiteSpace(id))
            {
                warnings.Add("RequiredLiquid 元素缺少 Liquid 字段");
                continue;
            }

            var litres = JsonHelpers.GetDouble(element, "VolumeInLitres", "Quantity", "Litres", "Liters", "Amount") ?? 0;
            yield return new Ingredient { ItemId = id, Kind = IngredientKind.Liquid, Quantity = litres, Units = "Litres" };
        }
    }

    /// <summary>RequiredTool（单值）/ OptionalTools（数组）。空串必须过滤掉。</summary>
    private static IEnumerable<Ingredient> ParseTools(JsonElement obj, List<string> warnings)
    {
        foreach (var element in JsonHelpers.GetArrayLike(obj, "RequiredTool", "RequiredTools"))
        {
            var id = JsonHelpers.AsString(element);
            if (string.IsNullOrWhiteSpace(id)) continue;   // "" 是「不需要工具」的写法
            yield return new Ingredient { ItemId = id, Kind = IngredientKind.Tool, Role = ToolRole.Required };
        }

        foreach (var element in JsonHelpers.GetArrayLike(obj, "OptionalTools", "OptionalTool"))
        {
            var id = JsonHelpers.AsString(element);
            if (string.IsNullOrWhiteSpace(id)) continue;
            yield return new Ingredient { ItemId = id, Kind = IngredientKind.Tool, Role = ToolRole.Optional };
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // 文本解码
    // ────────────────────────────────────────────────────────────────────

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        // UTF-16 / UTF-32 BOM 极少见，但出现了就得处理，否则整份文件解析失败
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes);

        // UTF-8 BOM 解码后是 U+FEFF，JsonDocument 不接受，必须先剥掉
        return text.TrimStart('\uFEFF');
    }
}
