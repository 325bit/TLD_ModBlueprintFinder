# 开发文档 · TLD 配方（蓝图）检索工具

> 玩家向的安装与使用说明见 [../README.md](../README.md)。
> 本文件写给要改代码或要做二次开发的人：数据格式、字段映射、分层设计、踩过的坑。

针对《漫漫长夜》(The Long Dark) + ModComponent 生态的只读检索工具：**输入一个物品 ID，双向列出「怎么做出它」和「它能用来做什么」。**

装了四十多个 modcomponent 之后，想知道 `GEAR_ScrapMetal` 到底能做什么，只能一个个 zip 解压翻 JSON。这个工具做掉这件事，并给出三组互不混淆的答案：

| 结果分组 | 含义 | 匹配字段 |
| --- | --- | --- |
| ① 作为产物 | 制作**这个物品**需要哪些蓝图 | `CraftedResult` |
| ② 作为原料 | **这个物品**被哪些蓝图消耗 | `RequiredGear[].Item` / `RequiredPowder[].Powder` / `RequiredLiquid[].Liquid` |
| ③ 作为工具 | 哪些蓝图需要 / 可以使用**这个物品**当作工具 | `RequiredTool` / `OptionalTools` / `AllowedCookingPots` |

工具与原料**刻意分开**：工具不会被扣掉，混进原料列表会让玩家误以为要消耗一把锤子。

---

## 1. 数据来源：`.modcomponent` 到底是什么

`.modcomponent` 是一个 **ZIP 容器**。这一点与参考实现 `ModComponentExtractor` 的结论完全一致 —— 它的 `Program.cs`
用 `ICSharpCode.SharpZipLib.FastZip` 把 `.modcomponent` 解压成目录来编辑，反向把目录压回 zip。

一个容器内部的典型结构：

```
AHandyToolbox.modcomponent  (ZIP)
├── BuildInfo.json              ← mod 元信息
├── blueprints/                 ← 蓝图定义，一个文件一份蓝图
│   └── AcornShelled.json
├── recipes/                    ← 烹饪食谱，蓝图嵌在 BlueprintData 里
│   └── bonebroth.json
├── auto-mapped/                ← prefab 自动映射（与配方无关）
├── gear-spawns/                ← 物品生成点（与配方无关）
├── localizations/              ← 本地化文本
└── bundle/                     ← 资产包
```

**只有 `blueprints/` 与 `recipes/` 参与索引**，其余目录直接跳过。

### 本工具与参考实现的差异（有意为之）

| | ModComponentExtractor | 本工具 |
| --- | --- | --- |
| 目的 | 解包 / 重打包 mod | 只读检索 |
| ZIP 处理 | SharpZipLib `FastZip` | BCL `System.IO.Compression.ZipArchive` |
| 是否落盘 | 解压成目录 | **完全不落盘**，内存中直读条目 |
| 依赖 | SharpZipLib | 零第三方依赖 |

不做落盘是硬性要求：工具不允许在任何情况下往 `Mods` 目录写文件。

### 实测数据规模（2.55 实机）

```
容器数量   : 46（全部打开成功）
JSON 条目  : 428
蓝图记录   : 428（其中 recipes/ 食谱 16 份）
涉及物品ID : 489
解析耗时   : ~130 ms
解析警告   : 2（AmmoCraftingTools 里有两份蓝图缺 Name 字段，已用文件名兜底）
```

---

## 2. 字段映射：JSON 里到底有哪些字段

以下映射不是猜的，是从 46 个真实 modcomponent、428 份蓝图 JSON 里统计出来的。

### 2.1 两种文件布局

**A. `blueprints/<名字>.json`** —— 顶层对象就是蓝图：

```json
{
  "Name": "AcornShelled",
  "RequiredGear": [ { "Item": "GEAR_Acorn", "Count": 10 } ],
  "OptionalTools": [ "GEAR_Hammer", "GEAR_BlacksmithHammer", "GEAR_StoneHammer" ],
  "RequiredCraftingLocation": "Anywhere",
  "CraftedResult": "GEAR_AcornShelled",
  "CraftedResultCount": 10,
  "DurationMinutes": 10
}
```

**B. `recipes/<名字>.json`** —— 食谱包装对象，蓝图嵌在 `BlueprintData` 里：

```json
{
  "RecipeName": "GAMEPLAY_Broth",
  "RecipeDescription": "GAMEPLAY_BrothDescription",
  "RequiredSkillLevel": 1,
  "AllowedCookingPots": [ "GEAR_CookingPot" ],
  "BlueprintData": {
    "Name": "REC_BoneBroth",
    "RequiredGear": [ { "Item": "GEAR_Bones", "Count": 3 } ],
    "RequiredPowder": [ { "Powder": "POWDER_Salt", "QuantityInKilograms": 0.01 } ],
    "RequiredLiquid": [ { "Liquid": "LIQUID_WaterPotable", "VolumeInLitres": 1.5 } ],
    "CraftedResult": "GEAR_UncookedBroth",
    "CraftedResultCount": 1,
    "DurationMinutes": 5
  }
}
```

**只解析 `blueprints/` 的工具会漏掉全部烹饪食谱** —— 这是这个格式最容易踩的坑。本工具两种都认，
并在「类型」列标注「蓝图 / 食谱」。

### 字段表

统计口径：46 个 modcomponent 中 `blueprints/` 目录下的蓝图记录（`recipes/` 走的字段相同）。

| JSON 字段 | 含义 | 出现次数 | 备注 |
| --- | --- | --- | --- |
| `Name` | 蓝图 ID（游戏内唯一键） | 404 | 可能缺失，缺失时用文件名兜底并记警告 |
| `CraftedResult` | **产物物品 ID** | 404 | 「作为产物」的匹配字段 |
| `CraftedResultCount` | 产物数量 | 404 | 缺省按 1 |
| `RequiredGear[].Item` | 原料物品 ID | 830 条 | 「作为原料」的匹配字段 |
| `RequiredGear[].Count` | 原料件数 | 805 | |
| `RequiredGear[].Quantity` + `Units` | 按重量计 | 64 | `Units` 实测有 `Kilograms` 与 `Count` 两种 |
| `RequiredPowder[].Powder` | 粉末 ID | 40 | 配 `QuantityInKilograms` |
| `RequiredLiquid[].Liquid` | 液体 ID | 52 | 配 `VolumeInLitres` |
| `RequiredTool` | 必需工具（单个字符串） | 385 | 空串 `""` 表示不需要，**必须过滤**，否则会把空 ID 索引进表 |
| `OptionalTools[]` | 可选工具 | 339 | |
| `RequiredCraftingLocation` | 制作地点 | 382 | 实测 `Workbench` / `WorkBench` 两种拼写并存，已归一化 |
| `KeroseneLitersRequired` | 煤油用量 | 325 | |
| `GunpowderKGRequired` | 火药用量 | 304 | 旧字段名 `GunpowderRequired` 另有 8 处，两个都读 |
| `AllowedCookingPots[]` | 可用锅具（食谱专用） | — | 归入「作为工具」 |

### 2.3 `RequiredGear` 的三种写法

同一个字段存在三种形态，解析器逐一兼容：

```jsonc
"GEAR_Acorn"                                                  // 纯字符串 → 1 件
{ "Item": "GEAR_Acorn", "Count": 3 }                          // 按件数
{ "Item": "GEAR_BirdEggRaw", "Quantity": 0.1, "Units": "Kilograms" }  // 按重量
```

第三种实测出现 25 次（集中在 `Bountiful_Foraging`）。**只认 `Count` 的实现会把这些原料的用量读成 1**，
虽然不影响检索命中，但显示出来的量是错的。

---

## 3. 双向检索的判定条件

匹配前先做归一化：`Trim()`、去掉 `OrdinalIgnoreCase` 的差异（物品 ID 是符号，但玩家手输不该被大小写惩罚）。

| 分组 | 判定 |
| --- | --- |
| ① 作为产物 | `blueprint.CraftedResult` 与查询串匹配 |
| ② 作为原料 | `RequiredGear[].Item`、`RequiredPowder[].Powder`、`RequiredLiquid[].Liquid` 任一项匹配 |
| ③ 作为工具 | `RequiredTool`、`OptionalTools[]`、`AllowedCookingPots[]` 任一项匹配 |

匹配强度分三级，结果按强度排序并在界面上可视化（斜体 + 变色 + 悬停提示）：

| 模式 | 命中范围 |
| --- | --- |
| 自动（默认） | 精确 > 前缀 > 包含，全部列出并分级 |
| 仅精确匹配 | 只出完全相等 |
| 仅子串匹配 | 只出「包含」且排除精确（用来捞一批同族 ID） |

**去重**：「合并同名蓝图」勾选后，跨 mod 的同名蓝图（实测 428 条记录里有 42 条重名，例如 `Bountiful_Foraging` 与
`DZ_Bountiful_Foraging` 是同一 mod 的两个版本）合并成一行，「来源 Mod」列显示全部来源，详情面板逐份列出。
取消勾选则一行一份，便于排查 mod 冲突。

---

## 4. 目录结构

```
TLD_ModBlueprintFinder/                ← 仓库根目录
├── README.md                          面向玩家的说明（安装 / 使用 / FAQ）
├── LICENSE
├── TLD_ModBlueprintFinder.sln
├── docs/                              ← 本文件所在目录
│   ├── DEVELOPMENT.md                 本文件：数据格式、字段映射、分层设计
│   ├── BUG-001-detail-switch.md       缺陷 001：详情面板切换失效
│   └── img/                           界面与复现截图
└── TLD_ModBlueprintFinder/            源码工程
    ├── TLD_ModBlueprintFinder.csproj
    ├── Program.cs                     入口（GUI / CLI 分流）
    ├── Cli.cs                         无界面模式
    ├── Models/
    │   └── BlueprintRecord.cs         BlueprintRecord / Ingredient / ContainerSummary / ScanReport
    ├── Parsing/
    │   ├── JsonHelpers.cs             宽容 JSON 取值（多字段名、字符串数字、单值/数组）
    │   ├── BlueprintParser.cs         蓝图映射：字段 → 模型，含 BlueprintData 解包
    │   └── ModScanner.cs              ZIP / 目录遍历、路径解析、条目分类
    ├── Indexing/
    │   ├── BlueprintIndex.cs          三张倒排表 + 匹配强度 + 相近 ID 建议
    │   └── BlueprintSearchEngine.cs   双向检索、聚合去重、SearchRow
    ├── Export/
    │   └── CsvExporter.cs             带 BOM 的 UTF-8 CSV
    └── Ui/
        ├── MainForm.Ui.cs             控件与列定义（code-only，无 .resx）
        └── MainForm.cs                事件、扫描、检索、设置持久化
```

分层原则：**界面层不做任何业务判断**，是否命中、为什么命中全部由 `Indexing` 决定；
`Parsing` 只负责把文件变成对象，不碰检索。

---

## 5. 编译与运行

### 环境要求

- Windows 10/11
- .NET SDK 8.0 或更高（目标框架 `net8.0-windows`，WinForms）
- 无任何 NuGet 依赖

### 编译

仓库根目录与工程目录同名（`TLD_ModBlueprintFinder/` 下再套一层 `TLD_ModBlueprintFinder/`），
命令行编译要进到**里层**那个工程目录：

```bat
cd TLD_ModBlueprintFinder\TLD_ModBlueprintFinder
dotnet build -c Release
```

或在 Visual Studio 2022 中打开仓库根目录的 `TLD_ModBlueprintFinder.sln` 后 F5。

### 运行

```bat
dotnet run --project TLD_ModBlueprintFinder
```

或直接双击 `TLD_ModBlueprintFinder\bin\Release\net8.0-windows\TLD_ModBlueprintFinder.exe`。

### 发布成单文件（可选）

```bat
dotnet publish TLD_ModBlueprintFinder -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

`--self-contained true` 可以打包运行时（约 70 MB），拷给别人不用装 .NET。

---

## 6. 使用步骤

1. **路径**：填游戏根目录（含 `Mods` 文件夹），或直接指向 `Mods` 目录，甚至单个 `.modcomponent`。
   不想手打就点 **自动定位** —— 它会读 Steam 注册表 + `libraryfolders.vdf`，扫出机器上所有安装，
   按「Mods 目录最近更新时间」排序，选最可能在用的那个并提示共有几份。
2. **扫描 Mods**：解析全部容器。完成后右侧显示扫描摘要（每个 mod 贡献多少条蓝图、哪些容器打不开、
   哪些蓝图字段有问题）。路径上次用过会自动记住并直接扫。
3. **输入物品 ID**，回车或点 **检索**。不知道全名就只输一部分，切到「自动」模式即可。
4. 结果分三个 Tab。点任意一行，右侧显示完整原料清单、工具、额外条件（煤油/火药/照明/技能等级）
   和**原始 JSON**，可以直接核对字段。
5. **导出 CSV**（三组结果一次导出，UTF-8 带 BOM，Excel 直接开）或 **复制**详情到剪贴板。

### 命令行模式

```bat
TLD_ModBlueprintFinder.exe --cli --path "F:\SteamLibrary\steamapps\common\TheLongDark" --item GEAR_Hammer
```

| 参数 | 说明 |
| --- | --- |
| `--path <路径>` | 游戏根目录 / Mods 目录 / 单个 modcomponent |
| `--item <物品ID>` | 要查询的物品 |
| `--mode auto\|exact\|fuzzy` | 匹配模式 |
| `--no-merge` | 不合并同名蓝图 |
| `--no-subfolders` | 不递归子目录 |
| `--no-folders` | 跳过解压目录形式的 mod |
| `--temp` | 额外扫描 `_ModComponentTemp` |
| `--scan-only` | 只扫描并打印统计 |
| `--csv <路径>` | 导出结果 CSV |

退出码：`0` 有结果，`1` 无匹配，`2` 参数错误，`3` 扫描异常，`4` 没解析到蓝图。

带图形界面时也可以预置参数，方便做桌面快捷方式：

```bat
TLD_ModBlueprintFinder.exe --path "F:\...\TheLongDark" --item GEAR_ScrapMetal
```

---

## 7. 验证方法（用你的机器自测）

在 2.55 实机（46 个容器 / 428 份蓝图）上的真实结果，可用来确认工具在你的环境里工作正常：

| 命令 | 期望结果 |
| --- | --- |
| `--item GEAR_ScrapMetal` | 产物 **3** 条、原料 **28** 条、工具 **0** 条 |
| `--item GEAR_Hammer` | 产物 **1** 条、原料 **0** 条、工具 **26** 条 |
| `--item GEAR_UncookedBroth` | 产物 **1** 条，来源标为「食谱」`recipes/bonebroth.json`，原料含 `POWDER_Salt 0.01 kg` 与 `LIQUID_WaterPotable 1.5 L` |
| `--item gear_scrapmetal`（全小写） | 与第一条完全一致，验证大小写不敏感 |
| `--item GEAR_Acorn` | 产物 4 条（1 精确 + 3 前缀/包含）、原料 5 条 |
| `--item GEAR_Acorn --mode exact` | 产物 **1** 条、原料 **2** 条 |
| `--item GEAR_Acorn --mode fuzzy` | 产物 3 条（排除精确那条） |
| `--item GEAR_AcornShel` | 前缀匹配命中 `GEAR_AcornShelled`，界面标为斜体/橙色 |

自检三步：

1. `--scan-only` 看「蓝图记录」是否在数百条量级。若为 0，先确认 `Mods` 里真有 `*.modcomponent`。
2. `--item GEAR_Hammer` 的「③ 作为工具」若为 0，说明只索引了部分 mod。
3. 随便点开一条，右侧原始 JSON 的 `CraftedResult` 应与「产物」列一致 —— 这是字段映射正确的直接证据。

---

## 8. 已知边界

- **不包含原版配方**。游戏本体的蓝图是 `ScriptableObject`，打包在 `tld_Data` 的资产里，不在任何 `modcomponent` 中。
  因此查原版物品可能得到「无匹配」——工具会在无结果时明确说明这一点，而不是假装没有配方。
  需要覆盖原版的话，得另行解析游戏资产，属于另一个工程。
- **只读**。不写 `Mods`、不写存档。唯一的写操作是 `%APPDATA%\TLD_ModBlueprintFinder\settings.json`
  （记住上次路径）和你主动导出的 CSV。
- **不校验 mod 是否生效**。DLL 类型 mod（不提供 modcomponent）不会出现在结果里。
- `_ModComponentTemp` 默认不扫：它是 ModComponent 加载时解压的副本，扫了会与 zip 内容重复计数。
  需要时用 `--temp` 或改 `ScanOptions.IncludeModComponentTemp`。
- 大小写不同的两个不同 ID 会被当成同一个（`OrdinalIgnoreCase`）—— 这是为玩家手输体验做的取舍。

---

## 9. 修复记录

### BUG-001（2026-10-25）：切回【作为产物】后详情不再切换

现象：查 `GEAR_SugarA`（1 产物 + 4 原料），在【作为原料】点开别的蓝图后切回【作为产物】，
右侧详情仍停在上一页签的蓝图，再点 `sugara` 那一行也没有反应。

原因有两层，缺一不可：

1. 详情刷新只挂在 `DataGridView.SelectionChanged` 上。点击**已经被选中**的行时选中集合没有变化，
   事件不触发 —— 于是「点击」这个动作被完全忽略。
2. 详情框是三个页签共用的一格，但没有任何代码在 `SelectedIndexChanged` 时把详情重新同步到
   「当前页签里选中的那一行」，所以切页签那一步就已经在显示别的页签的内容了。

修复（`Ui/MainForm.Ui.cs:348`、`Ui/MainForm.cs:611`）：

- 额外监听 `CellClick`，覆盖「点已选中行」这个盲区；
- 补 `TabControl.SelectedIndexChanged` → 按当前页签的表格重新渲染详情；
- 切到空页签时明确写「没有匹配结果」，而不是继续挂着别的页签的蓝图；
- 渲染前比对文本去重，避免连续触发把详情框的滚动位置弹回顶部。

完整分析、复现步骤、回归用例见 [BUG-001-detail-switch.md](BUG-001-detail-switch.md)。
