using System.Text;
using System.Text.Json;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>
/// Turns one JSON entry from a modcomponent into a <see cref="BlueprintRecord"/>.
/// Two layouts exist and both must be understood, or every cooking recipe is missed:
/// blueprints/&lt;name&gt;.json holds the blueprint at the root, while recipes/&lt;name&gt;.json wraps it in
/// "BlueprintData" and keeps RecipeName / AllowedCookingPots / RequiredSkillLevel outside.
/// Field counts and mapping notes: docs/DEVELOPMENT.md §2.
/// </summary>
public static class BlueprintParser
{
    /// <summary>Parse result for a single JSON entry.</summary>
    public readonly record struct ParseOutcome(BlueprintRecord? Record, string? SkipReason);

    /// <summary>
    /// Parses one entry. A non-null SkipReason means no record was produced; the caller reports it.
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

            // The root may be an array: some packers put several blueprints in one file.
            if (root.ValueKind == JsonValueKind.Array)
            {
                // Only the first element is returned here; use ParseMany for files that may hold several.
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

    /// <summary>A single file may hold several blueprints; this expands all of them.</summary>
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

    // Core mapping

    private static ParseOutcome Build(
        string modName,
        string containerPath,
        string entryPath,
        BlueprintSourceKind sourceKindHint,
        JsonElement root,
        string rawText)
    {
        // Step 1: detect the recipe wrapper - files under recipes/ nest the blueprint in BlueprintData.
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

            // AllowedCookingPots lives on the recipe, but it is a real "this item as a tool" reference.
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

        // Step 2: blueprint ID.
        var name = JsonHelpers.GetString(blueprintObj, "Name", "BlueprintName", "ID", "Id");
        if (string.IsNullOrWhiteSpace(name))
        {
            // Fall back to the file name but keep a warning, so the user knows the record is off-spec.
            name = Path.GetFileNameWithoutExtension(entryPath);
            warnings.Add("缺少 Name 字段，已用文件名兜底");
        }

        // Step 3: product.
        var crafted = JsonHelpers.GetString(blueprintObj,
            "CraftedResult", "Result", "CraftedResultGearName", "ResultItem", "Output", "OutputItem");
        // Without CraftedResult the record cannot answer "as a product": keep it, but flag the gap.
        if (string.IsNullOrWhiteSpace(crafted))
        {
            warnings.Add("缺少 CraftedResult 字段：无法参与「作为产物」检索");
            crafted = string.Empty;
        }

        var craftedCount = JsonHelpers.GetDouble(blueprintObj,
            "CraftedResultCount", "ResultCount", "OutputCount") ?? 1;

        // Step 4: ingredients.
        var ingredients = new List<Ingredient>();
        ingredients.AddRange(ParseGearRequirements(blueprintObj, warnings));
        ingredients.AddRange(ParsePowderRequirements(blueprintObj, warnings));
        ingredients.AddRange(ParseLiquidRequirements(blueprintObj, warnings));

        // Step 5: tools.
        var tools = new List<Ingredient>();
        tools.AddRange(ParseTools(blueprintObj, warnings));
        tools.AddRange(recipeExtraTools);

        // Step 6: extra requirements - not indexable as ingredients, but needed to plan the craft.
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
    /// RequiredGear accepts three shapes:
    ///   "GEAR_X"                                                     - one item
    ///   { "Item": "GEAR_X", "Count": 3 }                             - counts
    ///   { "Item": "GEAR_X", "Quantity": 0.1, "Units": "Kilograms" }  - weight
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

            // Units=Count means Quantity actually carries a count; normalize it into Count.
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
                // Neither present: assume one item, but leave a trace.
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

    /// <summary>RequiredPowder: [{ "Powder": "POWDER_Salt", "QuantityInKilograms": 0.01 }]</summary>
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

    /// <summary>RequiredLiquid: [{ "Liquid": "LIQUID_WaterPotable", "VolumeInLitres": 1.5 }]</summary>
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

    /// <summary>RequiredTool (single value) / OptionalTools (array). Empty strings must be filtered out.</summary>
    private static IEnumerable<Ingredient> ParseTools(JsonElement obj, List<string> warnings)
    {
        foreach (var element in JsonHelpers.GetArrayLike(obj, "RequiredTool", "RequiredTools"))
        {
            var id = JsonHelpers.AsString(element);
            if (string.IsNullOrWhiteSpace(id)) continue;   // "" means "no tool required"
            yield return new Ingredient { ItemId = id, Kind = IngredientKind.Tool, Role = ToolRole.Required };
        }

        foreach (var element in JsonHelpers.GetArrayLike(obj, "OptionalTools", "OptionalTool"))
        {
            var id = JsonHelpers.AsString(element);
            if (string.IsNullOrWhiteSpace(id)) continue;
            yield return new Ingredient { ItemId = id, Kind = IngredientKind.Tool, Role = ToolRole.Optional };
        }
    }

    // Text decoding

    /// <summary>Bytes to text. ItemDefinitionParser reuses the same BOM handling.</summary>
    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        // UTF-16/UTF-32 BOMs are rare but fatal: ignoring them fails the whole file.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes);

        // A UTF-8 BOM decodes to U+FEFF, which JsonDocument rejects; strip it first.
        return text.TrimStart('\uFEFF');
    }
}
