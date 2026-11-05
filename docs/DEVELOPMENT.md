# 开发文档 · TLD 配方（蓝图）检索工具

> 玩家向的安装与使用说明见 [../README.md](../README.md)。
> 本文件写给要改代码或要做二次开发的人：数据格式、字段映射、分层设计、踩过的坑。

针对《漫漫长夜》(The Long Dark) + ModComponent 生态的检索工具：**输入一个物品 ID，双向列出「怎么做出它」「它能用来做什么」，贴出它自己的 JSON 定义，并能就地把这两类 JSON 改回 modcomponent。**

装了四十多个 modcomponent 之后，想知道 `GEAR_ScrapMetal` 到底能做什么，只能一个个 zip 解压翻 JSON。这个工具做掉这件事，并给出四组互不混淆的答案：

| 结果分组 | 含义 | 匹配字段 |
| --- | --- | --- |
| ① 作为产物 | 制作**这个物品**需要哪些蓝图 | `CraftedResult` |
| ② 作为原料 | **这个物品**被哪些蓝图消耗 | `RequiredGear[].Item` / `RequiredPowder[].Powder` / `RequiredLiquid[].Liquid` |
| ③ 作为工具 | 哪些蓝图需要 / 可以使用**这个物品**当作工具 | `RequiredTool` / `OptionalTools` / `AllowedCookingPots` |
| ④ 物品定义 | 这个物品**本身**的参数（重量 / 腐坏 / 堆叠 / 可燃…） | `auto-mapped/<内部名>.json` 的文件名，见 §3 末 |

工具与原料**刻意分开**：工具不会被扣掉，混进原料列表会让玩家误以为要消耗一把锤子。
前三组是「配方」，第四组是「物品定义」——两套数据来自不同目录，互不推导，所以**有配方没定义、有定义没配方都是常态**。

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
├── auto-mapped/                ← 物品定义：预制体的组件参数（重量 / 腐坏 / 堆叠…）
├── gear-spawns/                ← 物品生成点（与配方无关）
├── localizations/              ← 本地化文本
└── bundle/                     ← 资产包
```

**收进索引的只有三类**：`blueprints/`、`recipes/`（构成配方倒排表）与 `auto-mapped/`（构成物品定义表）。
前两者回答「怎么造它」，后者回答「它本身是什么」，两套数据互不推导。其余目录直接跳过。

`auto-mapped/` 只取第一段目录名等于 `auto-mapped`（或 `auto_mapped`）的 `.json`，
`notes/`、`collectibles/`、`ClothingItems/` 等子目录同样收（命名规则一致，只是分类）。

### 本工具与参考实现的差异（有意为之）

| | ModComponentExtractor | 本工具 |
| --- | --- | --- |
| 目的 | 解包 / 重打包 mod | 检索（可选就地改单条 JSON） |
| ZIP 处理 | SharpZipLib `FastZip` | BCL `System.IO.Compression.ZipArchive` |
| 是否落盘 | 解压成目录 | **完全不落盘**，内存中直读条目 |
| 依赖 | SharpZipLib | 零第三方依赖 |

不做落盘是硬性要求：工具不允许在任何情况下往 `Mods` 目录写文件。

### 实测数据规模（2.55 实机）

```
容器数量      : 46（全部打开成功）
JSON 条目     : 1184（解析 1184，跳过 0）= 蓝图 423 + 物品定义 761
蓝图记录      : 423（其中 recipes/ 食谱 16 份）
物品定义      : 761 份（auto-mapped）
涉及物品 ID   : 480
解析耗时      : ~129 ms
解析警告      : 2（AmmoCraftingTools 里有两份蓝图缺 Name 字段，已用文件名兜底）
```

---

## 2. 字段映射：JSON 里到底有哪些字段

以下映射不是猜的，是从 46 个真实 modcomponent、423 份蓝图 JSON、761 份物品定义里统计出来的。

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

### 2.4 `auto-mapped/*.json`：物品定义（不是配方）

`auto-mapped/` 里放的是**物品预制体本身的组件参数**——重量、腐坏、可堆叠、可燃、能否烹饪——
与 `blueprints/` 的「怎么造它」是两套数据。查询物品 ID 时把这份 JSON 一并贴出来，改 mod 时不用再自己解压容器。

```json
{
  "ModGenericComponent": {
    "DisplayNameLocalizationId": "GAMEPLAY_SugarA",
    "WeightKG": 0.25,
    "DaysToDecay": 0,
    "MaxHP": 100.0,
    "InventoryCategory": "Food",
    "NormalModel": ""
  },
  "ModStackableBehaviour": { "MaxStackSize": 10 }
}
```

- 布局：`auto-mapped/<内部名>.json`；另有 `notes/`、`collectibles/`、`collectibles/cards/`、`ClothingItems/`
  四个子目录，只是分类，命名规则相同。
- 顶层是「组件名 → 组件参数」的字典，实测 40+ 种组件：
  `ModGenericComponent`（289）、`ModHarvestableBehaviour`（191）、`ModFoodComponent`（164）、
  `ModStackableBehaviour`（107）、`ModBurnableBehaviour`（79）……
- 本机扫描结果：46 个容器共 **761 份**定义。

**三个必须处理的现实问题**

1. **文件里没有预制体 ID**，只有 `DisplayNameLocalizationId` 之类的本地化 key，
   所以「这个 ID 对应哪份定义」只能靠文件名反推（见 §3 末）。
2. **文件名比 ID 少一个前缀**：文件叫 `sugara.json`，物品叫 `GEAR_sugara`；
   `GEAR_` / `LIQUID_` / `POWDER_` 都不写在文件里。
3. **尾逗号很常见**：Bountiful_Foraging 等 mod 导出的 JSON 带尾逗号，必须开 `AllowTrailingCommas`。
   真的解析失败也不丢内容——原文照样展示，另附一条提示，因为对二次开发的人来说
   一份有瑕疵但完整的 JSON 仍然比空白有用。

---

## 3. 双向检索的判定条件

匹配前先做归一化：`Trim()`、去掉 `OrdinalIgnoreCase` 的差异（物品 ID 是符号，但玩家手输不该被大小写惩罚）。

| 分组 | 判定 |
| --- | --- |
| ① 作为产物 | `blueprint.CraftedResult` 与查询串匹配 |
| ② 作为原料 | `RequiredGear[].Item`、`RequiredPowder[].Powder`、`RequiredLiquid[].Liquid` 任一项匹配 |
| ③ 作为工具 | `RequiredTool`、`OptionalTools[]`、`AllowedCookingPots[]` 任一项匹配 |
| ④ 物品定义 | `auto-mapped/<名字>.json` 的文件名（去掉前缀后）与查询串匹配，见下 |

匹配强度分三级，结果按强度排序并在界面上可视化（斜体 + 变色 + 悬停提示）：

| 模式 | 命中范围 |
| --- | --- |
| 自动（默认） | 精确 > 前缀 > 包含，全部列出并分级 |
| 仅精确匹配 | 只出完全相等 |
| 仅子串匹配 | 只出「包含」且排除精确（用来捞一批同族 ID） |

**去重**：「合并同名蓝图」勾选后，跨 mod 的同名蓝图（实测 423 条记录里有 42 条重名，例如 `Bountiful_Foraging` 与
`DZ_Bountiful_Foraging` 是同一 mod 的两个版本）合并成一行，「来源 Mod」列显示全部来源，详情面板逐份列出。
取消勾选则一行一份，便于排查 mod 冲突。

### ④ 物品定义怎么定 ID

文件名只有内部名，没有前缀，所以按可信度分三档反推宿主 ID，界面上「判定依据」列会明说用的是哪一档：

| 判定依据 | 含义 |
| --- | --- |
| 同 mod 配方引用 | 本容器的 `blueprints/`/`recipes/` 里出现过「前缀 + 文件名」，直接采用 |
| 其他 mod 引用 | 本容器没引用，但别处配方引用过同名 ID（物品与配方经常不在同一个 mod 里） |
| 仅文件名 | 谁都没引用过，只按名字展示，前缀未知 |

三档都没有时 `ItemId` 显示裸文件名，而不是替用户猜一个 `GEAR_` 上去——猜错比不猜更糟。

**大小写陷阱**：语料里同时存在 `GEAR_sugara`（MoreMeds 的物品）和 `GEAR_SugarA`（原版糖，被玫瑰果酱配方引用），
去掉前缀后文件名都是 `sugara`。所以：

- 反推时优先「大小写完全一致」的候选（`sugara` → `GEAR_sugara`）；
- 查询时若宿主 ID 与输入只差大小写、且语料里确实有同形异写的另一个 ID，
  该行标注「大小写不同」并在详情里点名提醒，避免把两件物品认成一件。

---

## 4. 目录结构

```
TLD_ModBlueprintFinder/                ← 仓库根目录
├── README.md                          面向玩家的说明（安装 / 使用 / FAQ）
├── LICENSE
├── TLD_ModBlueprintFinder.sln
├── publish.bat                        一键发布单文件自包含 exe → dist\
├── dist/                              发布产物（不入库）：TLD_ModBlueprintFinder.exe
├── docs/                              ← 本文件所在目录
│   ├── DEVELOPMENT.md                 本文件：数据格式、字段映射、分层设计
│   ├── BUG-001-detail-switch.md       缺陷 001：详情面板切换失效
│   └── img/                           界面与复现截图
└── TLD_ModBlueprintFinder/            源码工程
    ├── TLD_ModBlueprintFinder.csproj
    ├── Properties/PublishProfiles/
    │   └── SelfContainedSingleFile.pubxml   单文件 + 自包含发布配置
    ├── Program.cs                     入口（GUI / CLI 分流）
    ├── Cli.cs                         无界面模式
    ├── Models/
    │   ├── BlueprintRecord.cs         BlueprintRecord / Ingredient / ContainerSummary / ScanReport
    │   └── ItemDefinition.cs          ItemDefinition（auto-mapped 物品定义）/ ItemIds 前缀规则
    ├── Parsing/
    │   ├── JsonHelpers.cs             宽容 JSON 取值（多字段名、字符串数字、单值/数组）
    │   ├── BlueprintParser.cs         蓝图映射：字段 → 模型，含 BlueprintData 解包
    │   ├── ItemDefinitionParser.cs    物品定义：组件清单 + 原文，尾逗号容错
    │   └── ModScanner.cs              ZIP / 目录遍历、路径解析、条目分类
    ├── Indexing/
    │   ├── BlueprintIndex.cs          三张倒排表 + 匹配强度 + 相近 ID 建议
    │   ├── BlueprintSearchEngine.cs   双向检索、聚合去重、SearchRow
    │   └── ItemDefinitionIndex.cs     IDetailRow 接口 + 定义按文件名反推 ID
    ├── Export/
    │   └── CsvExporter.cs             带 BOM 的 UTF-8 CSV
    ├── Editing/
    │   └── ModBackup.cs               modcomponent 备份 + 条目写回（纯 BCL，可离线测）
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

或直接双击 `TLD_ModBlueprintFinder\bin\Debug\net8.0-windows\TLD_ModBlueprintFinder.exe`。

> ⚠️ **`bin\` 里的 exe 不能单独拷走。** 它是框架依赖的中间产物，必须和同目录的
> `TLD_ModBlueprintFinder.dll`、`.deps.json`、`.runtimeconfig.json` 待在一起，
> 只把 exe 复制到别处会起不来（现象：双击没反应或提示缺少 .NET / 找不到 dll）。
> 要分发的独立 exe 走下面的「发布」，产物在仓库根目录 `dist\`。

### 发布：单文件 + 自包含（分发用这个）

```bat
publish.bat
```

等价于：

```bat
dotnet publish TLD_ModBlueprintFinder -p:PublishProfile=SelfContainedSingleFile
```

配置在 `TLD_ModBlueprintFinder\Properties\PublishProfiles\SelfContainedSingleFile.pubxml`，
Visual Studio 的「发布」向导里也能直接选到。产物：`dist\TLD_ModBlueprintFinder.exe`，
**一个文件，约 63 MB**，目标机器不需要装 .NET，双击即跑。

| 设置 | 值 | 原因 |
| --- | --- | --- |
| `RuntimeIdentifier` | `win-x64` | 单文件必须先定 RID；x86/ARM 需要各发一份 |
| `SelfContained` | `true` | 把 .NET 运行时打进 exe —— 这是「拷到哪都能双击」的关键 |
| `PublishSingleFile` | `true` | 托管 dll 与运行时合并进同一个 exe |
| `IncludeNativeLibrariesForSelfExtract` | `true` | 原生库也塞进包里，首次运行解压到 `%TEMP%\.net\`，否则会漏几个 dll 在旁边 |
| `EnableCompressionInSingleFile` | `true` | 100 MB → 63 MB，代价是启动多约 0.8 s（实测总启动 1.1 s） |
| `PublishTrimmed` | `false` | WinForms 不支持裁剪，开了会在运行期炸反射 |
| `DebugType` | `none` | 不生成 pdb，保证 `dist\` 只有一个文件 |

实测：把 `dist\` 里那个 exe 单独复制到带空格与中文的空目录，`--cli --scan-only` 正常输出，
GUI 正常启动并在自己旁边生成 `TLD_ModBlueprintFinder_settings.json`。

**为什么 `dotnet build` 出来的不是单文件**：`PublishSingleFile` 只在 publish 阶段生效，
`build` 永远输出一堆文件。所以每次都走 `publish.bat`，别从 `bin\` 里拿 exe 去分发。

---

## 6. 使用步骤

1. **路径**：填游戏根目录（含 `Mods` 文件夹），或直接指向 `Mods` 目录，甚至单个 `.modcomponent`。
   不想手打就点 **自动定位** —— 它会读 Steam 注册表 + `libraryfolders.vdf`，扫出机器上所有安装，
   按「Mods 目录最近更新时间」排序，选最可能在用的那个并提示共有几份。
2. **扫描 Mods**：解析全部容器。完成后右侧显示扫描摘要（每个 mod 贡献多少条蓝图、哪些容器打不开、
   哪些蓝图字段有问题）。路径上次用过会自动记住并直接扫。
3. **输入物品 ID**，回车或点 **检索**。不知道全名就只输一部分，切到「自动」模式即可。
4. 结果分四个 Tab（产物 / 原料 / 工具 / 物品定义）。点任意一行，右侧显示完整原料清单、工具、
   额外条件（煤油/火药/照明/技能等级）和**原始 JSON**；物品定义页签显示的是物品自身的 JSON。
5. **导出 CSV**（四组结果一次导出，UTF-8 带 BOM，Excel 直接开）或 **复制**详情到剪贴板。
   物品定义行沿用同一套列，末尾多一列 `物品定义JSON`（配方行该列为空）。
6. **备份 / 就地改 JSON**：详情栏第二排按钮，详见 §10。

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

在 2.55 实机（46 个容器 / 423 份蓝图 / 761 份物品定义）上的真实结果，可用来确认工具在你的环境里工作正常：

| 命令 | 期望结果 |
| --- | --- |
| `--item GEAR_ScrapMetal` | 产物 **3** 条、原料 **28** 条、工具 **0** 条、物品定义 **1** 份（包含匹配到 `knifescrapmetal`） |
| `--item GEAR_Hammer` | 产物 **1** 条、原料 **0** 条、工具 **26** 条、物品定义 **2** 份（`blacksmithhammer`、`stonehammer`） |
| `--item GEAR_sugara` | 配方 **6** 条、物品定义 **1** 份，指向 `MoreMeds :: auto-mapped/sugara.json`，判定依据「同 mod 配方引用」 |
| `--item GEAR_SugarA` | 同一份定义，但多一个「大小写不同」标记 —— 原版糖没有自己的 JSON，反推过去的是 MoreMeds 那件 |
| `--item GEAR_Bones` | 原料 **3** 条、物品定义 **3** 份（`bones`、`orcabones1`、`orcabones2`，后两份靠包含匹配） |
| `--item GEAR_1firconefuel` | 配方 **0** 条、物品定义 **2** 份，判定依据为「仅文件名」—— 有物品没配方是常态 |
| `--item GEAR_UncookedBroth` | 产物 **1** 条，来源标为「食谱」`recipes/bonebroth.json`，原料含 `POWDER_Salt 0.01 kg` 与 `LIQUID_WaterPotable 1.5 L` |
| `--item gear_scrapmetal`（全小写） | 与第一条完全一致，验证大小写不敏感 |
| `--item GEAR_Acorn` | 产物 4 条（1 精确 + 3 前缀/包含）、原料 5 条、物品定义 3 份 |
| `--item GEAR_Acorn --mode exact` | 产物 **1** 条、原料 **2** 条 |
| `--item GEAR_Acorn --mode fuzzy` | 产物 3 条（排除精确那条） |
| `--item GEAR_AcornShel` | 前缀匹配命中 `GEAR_AcornShelled`，界面标为斜体/橙色 |

自检四步：

1. `--scan-only` 看「蓝图记录」是否在数百条量级、「物品定义」是否与之同量级。若为 0，先确认 `Mods` 里真有 `*.modcomponent`。
2. `--item GEAR_Hammer` 的「③ 作为工具」若为 0，说明只索引了部分 mod。
3. 随便点开一条，右侧原始 JSON 的 `CraftedResult` 应与「产物」列一致 —— 这是字段映射正确的直接证据。
4. 点开任意一条「④ 物品定义」，贴出的 JSON 顶层键应全是 `Mod*Component` / `Mod*Behaviour`。
   若键名是 `Name`/`CraftedResult`，说明 `IsDefinitionEntry` 把配方误判成了定义。

---

## 8. 已知边界

- **不包含原版配方**。游戏本体的蓝图是 `ScriptableObject`，打包在 `tld_Data` 的资产里，不在任何 `modcomponent` 中。
  因此查原版物品可能得到「无匹配」——工具会在无结果时明确说明这一点，而不是假装没有配方。
  需要覆盖原版的话，得另行解析游戏资产，属于另一个工程。
- **默认只读，写操作全在用户点按钮之后**。扫描、检索、导出都不会碰 `Mods`。
  会写文件的有三处：
  1. 程序所在目录的 `TLD_ModBlueprintFinder_settings.json`（记住上次路径与四个勾选项）；
  2. 程序所在目录 `TLDMBF_modcomponent_backup\` 下的备份（点「备份」或勾了自动备份时点「保存修改」）；
  3. 用户自己点「保存修改」后写回的 `.modcomponent`（详见 §10）。
  注意 1、2 的「程序所在目录」含义：把 exe 放进游戏根目录时，这两样都会落在游戏目录里，
  所以 README 里明确建议别放进去。要彻底不碰游戏目录，可以把配置改回 `ApplicationData`。
  路径取 `AppContext.BaseDirectory` 而不是 `Directory.GetCurrentDirectory()`：后者会被快捷方式的
  「起始位置」和命令行启动时的工作目录改写，配置会散落到各处；`BaseDirectory` 在单文件发布下也稳定指向 exe 同目录。
- **exe 放在只读位置时不报错也不落盘**：`SaveSettings()` 吞掉异常，代价是下次启动不记得上次路径。
  这是刻意的 —— 一个记不住路径的提示比一个弹窗更烦人。
- **单文件 exe 首次运行会往 `%TEMP%\.net\TLD_ModBlueprintFinder\<随机名>\` 解压约 8 MB 原生库**
  （`vcruntime140_cor3.dll`、`D3DCompiler_47_cor3.dll` 等），这是 `IncludeNativeLibrariesForSelfExtract`
  的既定行为。`%TEMP%` 被组策略锁死的机器会因此起不来，那种环境得改用框架依赖版。
- **1.2.0 之前的版本把配置放在 `%APPDATA%\TLD_ModBlueprintFinder\settings.json`**，新版不再读写该位置。
  升级后首次启动不会继承旧路径（只是「上次路径」这一项，重选一次即可），旧目录可手动删除。
- **不校验 mod 是否生效**。DLL 类型 mod（不提供 modcomponent）不会出现在结果里。
- `_ModComponentTemp` 默认不扫：它是 ModComponent 加载时解压的副本，扫了会与 zip 内容重复计数。
  需要时用 `--temp` 或改 `ScanOptions.IncludeModComponentTemp`。
- **配方检索**把大小写不同的两个 ID 当成同一个（`OrdinalIgnoreCase`）—— 这是为玩家手输体验做的取舍。
  代价是 `GEAR_SugarA` 与 `GEAR_sugara` 这类同形异写的物品在配方列表里会一起出现。
  物品定义那边不这么做：反推 ID 时优先大小写完全一致的候选，避免把两件物品的 JSON 装错。

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

---

## 10. 备份与就地修改

界面上的四个按钮（详情栏第二排）：编辑 JSON / 保存修改 / 备份该 modcomponent / 自动备份。
代码在 `Editing/ModBackup.cs`（备份 + 写回，纯 BCL，可脱离 WinForms 单独测）与 `Ui/MainForm.cs`（编辑态）。

### 10.1 备份

```
<程序目录>\TLDMBF_modcomponent_backup\<mod名>.modcomponent-<yyyyMMdd_HHmmss>
```

- **时间戳做后缀而不是建时间目录**：同一 mod 的历次备份在资源管理器里紧挨着排，
  一眼能看出改了几次；用目录就得逐层点进去。
- 同名同秒重复备份（连点两下按钮）→ 追加 `-2`、`-3`，**绝不覆盖已有备份**。
- 源是解压目录形式的 mod → 备份成同名**目录**（递归复制），命名规则相同。
- 源不是 `.modcomponent` 扩展名（少数 mod 用 `.zip`）→ 备份里保留扩展名，免得看不出格式。
- 备份就是整包字节复制，实测 md5 与原包一致；还原时把文件改回 `xxx.modcomponent` 丢回 `Mods` 即可。

### 10.2 为什么是「重写整包」而不是原地改

ZIP 没有原地替换条目的能力，`ZipArchiveMode.Update` 会把整个档案读进内存再整份写回，同样不是增量。
所以写入走三步：

```
读原包 → 逐条复制到 <name>.<guid>.tmp（目标条目换新内容）→ File.Move 覆盖原包
```

- 好处：**任何时刻原包都是完整的**。中途失败只会留下一个 tmp，下次写会重用它，不会出现「改了一半、
  游戏读不了」的包。
- 目录条目（`auto-mapped/` 这种以 `/` 结尾、长度为 0 的）必须原样复制，
  否则某些解包工具会看不到这些空目录。
- 已验证：重写后条目数不变，**只有目标条目的内容哈希变化**，其余条目内容逐条比对一致。
- 代价：整包重新压缩一遍。几十 MB 的容器要一两秒，界面在这期间是阻塞的（单文件工具，可接受）。

### 10.3 编码与 BOM

统一按 UTF-8 写；原条目带 BOM 就保留 BOM，否则不写。改的是文本，不该顺手改掉原作者的编码习惯。

### 10.4 编辑态：为什么把左侧表格冻住

`_edit` 不为 null 时：

- `_tabs.Enabled = false` —— 编辑期间切不了页签、点不了别的行；
- 详情框 `ReadOnly = false` 并换成淡黄底，按钮文案变「取消编辑」；
- `ShowDetail()` 第一行就是 `if (_edit is not null) return;` —— 双保险，防止任何残留事件把缓冲冲掉。

没有做「切换前弹窗问是否保存」那一套：状态机会复杂一个数量级，而用户照样可能点错。
让这段时间根本切不动，行为更好预期，代价只是编辑时少看一眼列表。

保存流程：

```
JSON 校验 →（勾了自动备份就备份）→ 写回容器 → 退出编辑态 → 全量重扫 → 自动重查
```

**必须重扫而不是局部更新**：索引里存的是解析后的原料/工具/产物，只换文本不重建索引，
界面会显示「明明改了却没生效」。

### 10.5 JSON 校验

用 `JsonDocument.Parse` + `AllowTrailingCommas` + `Skip` 注释，与解析器同一套宽容策略。
解析失败弹 Yes/No 并默认 No，但**允许硬存**：有些 mod 用游戏自己的宽松格式，
一刀切挡死反而碍事。校验只保证「语法能被 .NET 读懂」，不保证游戏认得。

### 10.6 已知取舍

- 合并同名蓝图的行（一份蓝图有多个副本）只写第一份（`Primary`），状态栏会写明合并了几份。
- 备份不会自动清理，反复改同一个 mod 会一直积累。
- 游戏运行时写会失败（文件被占用），错误直接弹出来，原包不动。
- 写回后 zip 内所有条目的压缩结果都会变，`git` 之类按字节比较的版本控制会显示整包变动 —— 这是重写的必然结果。

### 10.7 怎么自测这段

`Editing/ModBackup.cs` 不依赖 WinForms，可以建控制台桩工程链接它跑真实容器（参考
`projects/ContainerSorterStub` 的做法）。值得覆盖的点：备份命名与字节一致、写回后条目数不变、
非目标条目哈希不变、BOM 保持、写不存在的条目要报错且原包仍可读、目录型容器走直接写文件。
