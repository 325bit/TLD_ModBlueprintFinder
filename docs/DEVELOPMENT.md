# TLD Mod Blueprint Finder - Developer Documentation

> Language: English only - code comments, `docs/` and CHANGELOG.
> Player-facing guide: [../README.md](../README.md). Maintainer reading order: this file, then the bug notes.

Version 1.7.0 · target game 2.55 · .NET 8 (`net8.0-windows`, WinForms) · no third-party dependencies.

Companion docs: [BUG-001-detail-switch.md](BUG-001-detail-switch.md) - why the shared detail panel used to keep
showing another tab's row; [BUG-002-unsaved-prompt.md](BUG-002-unsaved-prompt.md) - why untouched CRLF entries
were reported as edited. Comment migration map: §11.

The tool searches the `modcomponent` files a modded The Long Dark install carries, plus the game's own
recipes from a snapshot embedded in the exe (§2.5). Give it one item ID and it answers six questions that
never mix:

| Group | Question | Match field | Scope |
| --- | --- | --- | --- |
| 1 as product (blueprint) | Which blueprints make **this item** | `CraftedResult` | `blueprints/` |
| 2 as ingredient (blueprint) | Which blueprints consume **this item** | `RequiredGear[].Item` / `RequiredPowder[].Powder` / `RequiredLiquid[].Liquid` | `blueprints/` |
| 3 as tool | Which recipes need or accept **this item** as a tool | `RequiredTool` / `OptionalTools` / `AllowedCookingPots` | both |
| 4 as product (recipe) | Which dish has **this item** as its result | `CraftedResult` | `recipes/` |
| 5 as ingredient (recipe) | Which dishes use **this item** as food | same as 2 | `recipes/` |
| 6 item definition | What the item **itself** is (weight, decay, stack, burn) | file name under `auto-mapped/` | `auto-mapped/` |

Every group holds two kinds of row: mod records and vanilla records. Vanilla rows are always sorted to the
bottom of their group and are read-only (§2.5).

Tools are kept out of the ingredient groups on purpose: a tool is not consumed, and listing it there reads
as "bring a hammer to burn it". Blueprints and recipes are split because the JSON shape is the same but the
player's question is not: "what tools can this meat make" is not "what can this meat cook into". Groups 1,
2, 4 and 5 never overlap, so `TotalHits` can be summed; group 3 stays merged because a cooking pot hangs off
a recipe while a hammer hangs off a blueprint, and that is one question for the player.

## 1. File map

```
TLD_ModBlueprintFinder/                 repository root
├── README.md                           player-facing, bilingual
├── LICENSE                             MIT
├── publish.bat                         one-click single-file publish -> dist\
├── dist/                               release artifact (not in git)
├── data/                               vanilla snapshot, generated (see §2.5)
│   ├── vanilla/blueprints/*.json       104 craftables in the modcomponent shape
│   ├── vanilla/recipes/*.json          29 dishes in the modcomponent shape
│   ├── vanilla/manifest.json           game build, counts, export time
│   └── vanilla.zip                     the same tree, embedded into the exe
├── tools/export_vanilla.py             reads the game's bundles, writes data/ (§2.5)
├── docs/                               this file, bug notes, screenshots
└── TLD_ModBlueprintFinder/             project
    ├── TLD_ModBlueprintFinder.csproj
    ├── Properties/PublishProfiles/SelfContainedSingleFile.pubxml
    ├── Program.cs                      entry point; GUI / CLI split
    ├── Cli.cs                          headless mode and exit codes
    ├── Models/BlueprintRecord.cs       BlueprintRecord / Ingredient / ContainerSummary / ScanReport
    ├── Models/ItemDefinition.cs        ItemDefinition / ItemIds prefix rules
    ├── Parsing/JsonHelpers.cs          lenient JSON accessors
    ├── Parsing/BlueprintParser.cs      JSON -> BlueprintRecord, including BlueprintData unwrapping
    ├── Parsing/ItemDefinitionParser.cs component list + raw text, trailing-comma tolerant
    ├── Parsing/ModScanner.cs           ZIP / folder walk, path resolution, entry classification
    ├── Parsing/VanillaArchive.cs       reads the embedded vanilla.zip through the same parser
    ├── Indexing/BlueprintIndex.cs      three inverted tables + kindFilter + similar-ID suggestions
    ├── Indexing/BlueprintSearchEngine.cs  five groups, dedup, SearchRow
    ├── Indexing/ItemDefinitionIndex.cs IDetailRow + ID inference from file names
    ├── Export/CsvExporter.cs           UTF-8 CSV with BOM
    ├── Editing/ModBackup.cs            whole-container copies into TLDMBF_modcomponent_backup\
    ├── Editing/ModComponentArchive.cs  entry CRUD (ZIP rewrite core + folder branch)
    └── Ui/                             MainForm.Ui.cs (controls), MainForm.cs (logic),
                                        ContainerEditorForm.cs, Theme.cs (palettes, §8.1),
                                        ThemedTabControl.cs (native tab chrome overpaint, §8.1)
```

Layering rule: the UI makes **no** business decisions. Whether a row matched and why is decided in
`Indexing`; `Parsing` only turns bytes into objects and never searches. `Editing` knows nothing about
WinForms, so it can be exercised from a console stub.

## 2. Data and fields

A `.modcomponent` is a **ZIP container**. Entries worth indexing:

```
<mod>.modcomponent  (ZIP)
├── BuildInfo.json              mod metadata
├── blueprints/                 one blueprint per file
├── recipes/                    cooking recipes, blueprint nested in BlueprintData
├── auto-mapped/                item definitions (prefab component parameters)
├── gear-spawns/                spawn points - not indexed
├── localizations/              text - not indexed
└── bundle/                     assets - not indexed
```

`auto-mapped/` is matched on the first path segment only (`auto-mapped` or `auto_mapped`); `notes/`,
`collectibles/`, `collectibles/cards/` and `ClothingItems/` below it are the same format, only categorized
differently. A same-named JSON anywhere else is not treated as a definition.

### 2.1 Two entry layouts

**A. `blueprints/<name>.json`** - the root object is the blueprint:

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

**B. `recipes/<name>.json`** - a wrapper, the blueprint sits in `BlueprintData`:

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

A parser that only knows layout A loses **every cooking recipe**. `AllowedCookingPots` lives on the wrapper
but is a real "this item as a tool" reference, so it is folded into the tool list.

### 2.2 Field map

Counts below come from 46 real modcomponents (the `blueprints/` set; `recipes/` uses the same fields).

| JSON field | Meaning | Occurrences | Notes |
| --- | --- | --- | --- |
| `Name` | blueprint ID (unique key) | 404 | may be missing; falls back to the file name plus a warning |
| `CraftedResult` | **product item ID** | 404 | group 1 / 4 match field |
| `CraftedResultCount` | product amount | 404 | defaults to 1 |
| `RequiredGear[].Item` | ingredient ID | 830 entries | group 2 / 5 match field |
| `RequiredGear[].Count` | ingredient count | 805 | |
| `RequiredGear[].Quantity` + `Units` | ingredient weight | 64 | `Units` is `Kilograms` or `Count` in the wild |
| `RequiredPowder[].Powder` | powder ID | 40 | paired with `QuantityInKilograms` |
| `RequiredLiquid[].Liquid` | liquid ID | 52 | paired with `VolumeInLitres` |
| `RequiredTool` | required tool (single string) | 385 | `""` means none - must be filtered, or an empty ID enters the index |
| `OptionalTools[]` | alternative tools | 339 | |
| `RequiredCraftingLocation` | crafting station | 382 | both `Workbench` and `WorkBench` occur; normalized to one |
| `KeroseneLitersRequired` | kerosene needed | 325 | shown as an extra requirement |
| `GunpowderKGRequired` | gunpowder needed | 304 | old name `GunpowderRequired` also occurs (8 times); both are read |
| `AllowedCookingPots[]` | usable pots (recipes only) | - | folded into group 3 |

### 2.3 `RequiredGear` has three shapes

```jsonc
"GEAR_Acorn"                                                          // bare string -> one item
{ "Item": "GEAR_Acorn", "Count": 3 }                                  // counted
{ "Item": "GEAR_BirdEggRaw", "Quantity": 0.1, "Units": "Kilograms" }  // by weight
```

The third form is frequent enough (25 occurrences, mostly `Bountiful_Foraging`) that a parser reading only
`Count` reports a wrong amount: the search still hits, but the displayed quantity is a lie.

### 2.4 `auto-mapped/*.json` - item definition, not recipe

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

- The root is a dictionary of component name -> parameters; over 40 component types occur
  (`ModGenericComponent` 289, `ModHarvestableBehaviour` 191, `ModFoodComponent` 164,
  `ModStackableBehaviour` 107, `ModBurnableBehaviour` 79, ...).
- **No prefab ID is stored.** Only localization keys are, so "which ID does this file belong to" has to be
  inferred - see §6.
- The file name is one prefix short of the ID: `sugara.json` describes `GEAR_sugara`, and `GEAR_` /
  `LIQUID_` / `POWDER_` never appear inside the file.
- Trailing commas are common (export habit of several mods), so parsing must allow them. A genuine syntax
  error is not fatal either: the raw text is still shown with a warning, because for a modder a slightly
  malformed but complete JSON beats an empty panel.

JSON is never mapped to strong types on purpose: component fields vary per mod, and showing the text as
written is more useful (and can be pasted straight into someone else's mod).

### 2.5 Vanilla snapshot (`data/vanilla.zip`)

The game's own blueprints are not files: each one is a `BlueprintData` ScriptableObject inside an
Addressables bundle (`Assets/ScriptableObjects/Gear/Blueprints/BLUEPRINT_*.asset`). Two consequences, and
the second is why the tool ships data instead of reading the game at runtime:

1. Reading them needs UnityPy plus a CAB index over all 533 bundles (~50 s, only interesting to a build).
2. A released exe is one file. Teaching it to parse Unity bundles would add a large, game-version-fragile
   dependency to every player's download to read data that changes only when the game itself updates.

So `tools/export_vanilla.py` runs on the maintainer's machine, writes the same ModComponent JSON shape the
mod components use (`blueprints/*.json`, `recipes/*.json`, plus `manifest.json` with the build string), zips
it to `data/vanilla.zip` (60 KB) and the project embeds that zip as a resource. `VanillaArchive` unpacks it
through **the same `BlueprintParser`** - one parser means the two sources cannot drift apart in field
handling, and the export script only has to emit one shape.

Re-run after a game update:

```bat
python tools/export_vanilla.py --game "F:/GameAbouts/The Long Dark/TheLongDark_2.55"
```

The script prints its own sanity numbers (`blueprints: 104 (decoration results 45), recipes: 29, failed: 0,
weighted gear lines: 33`). Three field traps are silent - the export succeeds and every row still looks
plausible - so they are recorded here rather than in the script alone:

| Trap | What happens if ignored | Rule |
| --- | --- | --- |
| `m_CraftingResultType` picks the product field | all 45 furniture blueprints reported `GEAR_CougarWrap`, the stale prefab left in `m_CraftedResultGear` | `StandardGear` (0) -> `m_CraftedResultGear`; `Decoration` (1) -> `m_CraftedResultDecoration` |
| `RequiredGearItem.m_Units` picks count vs weight | 33 lines carry `Units.Kilograms`, where `m_Count` is 0 and the amount sits in `m_Weight` | emit `{Item, Quantity, Units:"Kilograms"}` for those |
| the fixed-point scale | 1e8 instead of 1e9 multiplies every requirement by ten: broth becomes 10 kg of meat, a pie 1.5 L of water | see §2.5.1 |

Vanilla rows are **read-only by construction**: `BlueprintRecord.IsVanilla` is set by the archive loader,
`ContainerPath` is empty (there is no file to write to), and `UpdateEditButtons` checks `IsVanilla` on top of
the empty path so a future change to the container handling cannot quietly make them editable. The container
editor stays available - it is not row-scoped - but opens on the first container while a vanilla row is
selected, and its tooltip says so.

#### 2.5.1 Fixed-point scale is 1e9, not 1e8

`ItemWeight.m_Units` and `ItemLiquidVolume.m_Units` are `long` fixed point. The literal comes from the
game's own static constructor, not from a tolerance test: `ItemLiquidVolume..cctor` (RVA `0x36482E0`) writes
`Liter = 0x3B9ACA00` (1e9) and `Centiliter = 0x989680` (1e7). Cross-check with values a player can read off
the crafting screen: broth needs 1.0 kg of meat and 1.5 L of water, a meat pie 0.5 kg of meat + 0.2 kg of
flour + 0.15 L of water + 0.05 L of oil. All three land exactly there at 1e9 and are ten times too large at
1e8 - the wrong scale does not look wrong per row, which is why it survived a full export undetected.

## 3. Pipeline: from a path to a rendered row

```
MainForm.ScanAsync
  └─ ModScanner.Scan            path -> ContainerSpec[] -> per-container parse
       ├─ ResolveInput          game root / Mods / single file -> containers
       ├─ ScanZip | ScanFolder  enumerate entries, size guard, decode
       ├─ BlueprintParser.ParseMany     -> BlueprintRecord[]
       ├─ ItemDefinitionParser.Parse    -> ItemDefinition[]
       └─ VanillaArchive.Load           embedded vanilla.zip -> BlueprintRecord[] (IsVanilla, §2.5)
  └─ BlueprintIndex + ItemDefinitionIndex     inverted tables built once per scan
  └─ BlueprintSearchEngine.Search             five groups + definitions
  └─ MainForm.ApplyReport                     bind rows, tabs, status, detail
```

Nothing is written during this chain. The indexes are rebuilt from scratch after a write, because they hold
parsed ingredients and products, not text - swapping the text alone would show "saved but nothing changed".

## 4. Contact points - no game injection

This tool loads no assemblies, patches nothing and never runs inside the game. Its only contact with the
game folder is the file system:

| Direction | Path | When |
| --- | --- | --- |
| read | `<path>\Mods\**\*.modcomponent`, `*.zip`, extracted mod folders | scan, and every read in the container editor |
| read | the `vanilla.zip` embedded in the exe | once per process, on the first scan |
| write | `<program dir>\TLD_ModBlueprintFinder_settings.json` | after a scan / on close; failures are swallowed |
| write | `<program dir>\TLDMBF_modcomponent_backup\<mod>.modcomponent-<stamp>` | "Back up", or "Save changes" with auto-backup on |
| write | the `.modcomponent` the user edited | only on an explicit "Save changes" / editor action |

The vanilla snapshot is read from the assembly, never from the game folder: a player can point the tool at any
path, or at none, and the vanilla answers stay the same.

`AppContext.BaseDirectory` is used instead of `Directory.GetCurrentDirectory()`: the latter is rewritten by
a shortcut's "Start in" and by the shell, which would scatter the settings file. BaseDirectory also resolves
to the exe folder under single-file publish.

If the exe sits in the game root, the settings file and the backup folder land in the game folder too - the
README tells players to keep the exe elsewhere.

## 5. Search model

Normalization before matching: `Trim()` plus `OrdinalIgnoreCase`. Item IDs are case-sensitive symbols, but a
hand-typed query must not be punished for casing. Item definition lookup is stricter (§6).

| Group | Filter |
| --- | --- |
| 1 as product (blueprint) | `SourceKind == Blueprint` and `CraftedResult` matches |
| 2 as ingredient (blueprint) | same kind, and any of the three ingredient groups matches |
| 3 as tool | any of `RequiredTool` / `OptionalTools[]` / `AllowedCookingPots[]` matches (both kinds) |
| 4 as product (recipe) | `SourceKind == Recipe` and `CraftedResult` matches |
| 5 as ingredient (recipe) | same kind, and any ingredient group matches |
| 6 item definition | file stem matches after prefix stripping |

Filtering happens in `BlueprintIndex.SearchAsProduct` / `SearchAsIngredient` through `kindFilter`;
`BlueprintSearchEngine` calls `CollectProducts` / `CollectIngredients` twice (Blueprint once, Recipe once).
The dedup key and the sort order therefore exist once. Writing them twice per tab is how "tab says 3, table
shows 4 rows" happens.

Match tiers, visualized as italics plus color plus tooltip:

| Mode | Range |
| --- | --- |
| Auto (default) | exact > prefix > contains, everything listed and graded |
| Exact only | identical IDs only |
| Fuzzy | contains only, exact excluded - used to sweep a family of IDs |

**Dedup**: with "merge duplicate names" on, the same blueprint shipped by several mods collapses into one row
(42 of the 423 records share a name; `Bountiful_Foraging` and `DZ_Bountiful_Foraging` are two versions of one
mod). The "mod" column lists every source and the detail panel shows each copy. Turn it off to inspect mod
conflicts, one row per copy.

**Vanilla rows never merge.** The dedup key is empty for them, so a mod blueprint and the vanilla blueprint it
overrides stay two rows. Merging would print `SomeMod | <vanilla source>` in the source column and hide exactly the
distinction the feature exists to show. Ordering is "mod first, vanilla after", applied before match quality:
a vanilla row that matched exactly must not jump above the mod row that actually added the item.

**Suggestions**: when nothing matches, candidate IDs are scored by bidirectional containment on the
prefix-stripped name plus Levenshtein distance, so `Acorn` still finds `GEAR_Acorn`.

## 6. Item definition identity

The file name carries no prefix, so the host ID is inferred in three tiers, and the "basis" column says
which tier was used:

| Basis | Meaning |
| --- | --- |
| same-mod recipe reference | this container's `blueprints/` or `recipes/` contains prefix + file name; taken as-is |
| other mod reference | this container does not reference it, but some recipe elsewhere does (items and recipes often ship in different mods) |
| file name only | nothing references it; shown by name, prefix unknown |

When all three tiers fail, `ItemId` shows the bare file name instead of guessing a `GEAR_` prefix - a wrong
guess is worse than no guess.

**Case trap**: the corpus contains `GEAR_sugara` (a MoreMeds item) and `GEAR_SugarA` (vanilla sugar,
referenced by a rosehip jam recipe). Stripped of the prefix both are `sugara`. Therefore:

- the inference prefers an exact-case candidate (`sugara` -> `GEAR_sugara`);
- a query that differs from the host ID only by case is marked "different case" when another ID with the
  same lower-case form exists, and the detail panel names the two, so the two items are not merged silently.

## 7. Constraint system: writing back

### 7.1 Backups

```
<program dir>\TLDMBF_modcomponent_backup\<mod>.modcomponent-<yyyyMMdd_HHmmss>
```

- A stamp suffix rather than a folder per run: successive backups of one mod sit next to each other in
  Explorer and the number of edits is visible at a glance.
- A second backup in the same second gets `-2`, `-3`; an existing backup is never overwritten.
- An extracted-folder mod is backed up as a directory tree, same naming rule.
- Sources that are not `.modcomponent` (a few mods ship `.zip`) keep their extension, so the format stays
  obvious.
- A backup is a byte copy; restoring means renaming it back to `xxx.modcomponent` and dropping it into `Mods`.

### 7.2 Why the whole archive is rewritten

A ZIP cannot replace one entry in place, and `ZipArchiveMode.Update` reads the whole archive into memory and
writes it back anyway. So a write is three steps:

```
read original -> copy entry by entry into <name>.<guid>.tmp (target swapped / dropped / renamed)
              -> File.Move over the original
```

- The original is complete at every moment. A failure leaves a temp file, never a container that is half
  written and unloadable.
- Directory entries (`auto-mapped/` and friends, zero length, trailing `/`) must be copied verbatim, or some
  unpackers lose the empty folders.
- Verified: entry count is unchanged after a rewrite, only the target entry's hash differs, and every other
  entry compares equal byte for byte.
- Cost: the whole archive is recompressed. Tens of MB take a second or two, and the UI is blocked meanwhile
  (acceptable for a single-file desktop tool).

### 7.3 Encoding, BOM and directory entries

Text is written as UTF-8; a BOM is preserved when the original entry had one and omitted otherwise. Editing
text should not silently change the author's encoding habit.

ZIP does not require directory entries, but many unpackers (including ModComponent's own extraction) rely on
them to restore structure. Writing `blueprints/sub/deep/x.json` therefore also writes the missing
`blueprints/sub/` and `blueprints/sub/deep/` entries - only the missing ones, never touching existing ones.

Binary entries are detected by a NUL byte in the content. They are listed (behind the "json only" filter) and
shown read-only. Forcing a bundle, font or texture through a text editor yields mojibake and corrupts it on
write.

### 7.4 Three gates against losing unsaved edits

`TryLeaveCurrent()` runs before switching entries, switching containers and closing the window. "Cancel"
must abort the caller: the entry list restores its selection and the combo box restores
`_lastContainerIndex`. A failed save (validation refused, or the write threw) also returns false - losing
user edits because saving failed is not acceptable.

Filtering never loses content either: when the current entry is filtered out and there are no unsaved
changes, the list jumps to the first entry so filtering doubles as locating; with unsaved changes the
selection is cleared but the editor keeps its text, and clearing the filter finds it again.

The main window instead freezes the left table while one row is being edited (`_tabs.Enabled = false`,
`ShowDetail()` returns early). A "save before switching?" state machine would be an order of magnitude more
complex and users can still click wrong; making the switch impossible is far more predictable.

The baseline behind the gate must be read from the control, not from the file. `RichTextBox.Text` reports LF
for every line break while the file usually holds CRLF, so a file-sourced baseline marks 75% of entries as
edited the moment they load. Both editors therefore set `_original` / `EditSession.OriginalText` to
`editor.Text` after assigning the content. See BUG-002 for the measurement.

## 8. Settings

`TLD_ModBlueprintFinder_settings.json`, next to the exe.

| Field | Default | Effect |
| --- | --- | --- |
| `LastPath` | null | last scanned path; scanned automatically on the next start |
| `MergeDuplicates` | `true` | merge same-named blueprints across mods |
| `IncludeSubFolders` | `true` | also look for containers in subfolders of `Mods` |
| `MatchModeIndex` | `0` | combo-box index: 0 auto, 1 exact, 2 fuzzy |
| `AutoBackup` | `true` | back up before every write |
| `DarkMode` | `false` | dark colour scheme; same as the night-mode checkbox in the path row |

A failed save is swallowed on purpose: a tool that forgets the path is less annoying than a modal dialog on
every launch out of a read-only folder.

### 8.1 Two colour schemes

`Ui/Theme.cs` holds two `Palette` instances plus the code that pushes one into a live window. Light is the
original look value for value; dark is the night variant. Nothing else in the UI knows a scheme exists:
controls read `Theme.Current` where they used to write a literal, and `Theme.Apply(form)` re-assigns every
colour WinForms would otherwise keep after the first paint.

Four things are not expressible as a palette entry, and each one needed its own mechanism:

| Where | Why a colour is not enough | What is done instead |
| --- | --- | --- |
| Title bar | drawn by DWM, not by the window | `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`: attribute 20, 19 as fallback |
| Scroll bars | painted from the control's window theme | `SetWindowTheme(hwnd, "DarkMode_Explorer", null)`, reset to `null` for light |
| Tab strip | drawn by the system; `BackColor` never reaches it | owner-draw, plus `TabAppearance.FlatButtons` because Normal paints a white etched page border; the parts owner-draw cannot reach are overpainted, see below |
| Column headers | the header above the current cell takes the row *selection* colour | `ColumnHeadersDefaultCellStyle.SelectionBackColor` set to the header colour |

#### The tab frame that no colour reaches

Owner-draw covers the tab rectangles and nothing else. Three surfaces around them are painted by the native
tab control with `SystemColors.Control` (`240,240,240`) no matter what `BackColor`, `Appearance` or the
window theme say, and they showed up as a light outline around the whole result area:

| Surface | Size in the capture | Painted by |
| --- | --- | --- |
| frame around the page area | 4 px left / right / bottom, 2 px top | native page frame |
| empty strip right of the last tab | rest of the row | native strip filler |
| frame of every tab | 2 px | native tab frame, drawn *after* `WM_DRAWITEM` |

`ThemedTabControl.WndProc` therefore paints after `base.WndProc` on `WM_PAINT`, on a device context from
`GetDC` (the update region is already validated at that point, `BeginPaint` would clip the overpaint away):
the whole strip goes to `Palette.Window`, every tab is redrawn through `Theme.PaintTab`, and the page frame
goes to `Palette.Surface` so it merges with the page. The method returns immediately when `Theme.IsDark` is
false - light keeps the native drawing and is byte-identical to the pre-theming build (measured: 0 differing
pixels; the dark capture differs only inside the tab control's own rectangle, 16378 px).

Two alternatives were measured and rejected:

- `SetWindowTheme(hwnd, "", "")` removes the page frame but leaves the strip filler light, and the tabs fall
  back to a 3D outline.
- `Multiline = true` removes the native scroll arrows, but an owner-drawn multiline strip silently drops its
  last tab (comctl32 reports no rect for it), so a narrow window would hide a page.

Known leftover: when the strip overflows, comctl32 puts an `msctls_updown32` scroll pair in the corner of the
row. That is a **child window**, so it paints after the parent and cannot be overpainted; it stays system grey
(332 px measured at an 800 px strip). Hiding it would make the overflowing tabs unreachable, and neither
`SetWindowTheme(updown, "", "")` nor `DarkMode_Explorer` darkens it.

Restoration rule: light has to come back identical to the build before theming existed. Every dark-only
tweak therefore has a light branch in the same method rather than a separate reset pass - flat buttons go
back to `UseVisualStyleBackColor`, the detail box goes back to `Fixed3D`, header separators go back to
`Raised`, tabs go back to `Appearance.Normal`.

The button trap is worth spelling out: a `Button` with `UseVisualStyleBackColor = true` ignores `BackColor`
entirely, so the dark branch has to clear the flag before assigning a colour, and the light branch has to set
it again or the button keeps the flat square look.

`StatusTone` exists for the same reason as the palette: call sites say *what* the status line means (`Ok`,
`Warn`, `Error`, `Muted`), the palette decides what that looks like, and the last tone is remembered so a
scheme switch recolours the current message instead of leaving the previous scheme's colour on screen.

## 9. Diagnostics: CLI and exit codes

```bat
TLD_ModBlueprintFinder.exe --cli --path "<game root | Mods | one .modcomponent>" --item GEAR_Hammer
```

| Argument | Meaning |
| --- | --- |
| `--path <path>` | game root, `Mods` folder, or a single modcomponent |
| `--item <id>` | item to look up |
| `--mode auto\|exact\|fuzzy` | match mode |
| `--no-merge` | do not merge same-named blueprints |
| `--no-subfolders` | do not recurse into subfolders |
| `--no-folders` | skip extracted-folder mods |
| `--temp` | also scan `_ModComponentTemp` |
| `--no-vanilla` | do not load the embedded vanilla snapshot |
| `--scan-only` | scan and print statistics only |
| `--csv <path>` | export the result as CSV |

Exit codes: `0` results found, `1` no match, `2` bad arguments, `3` scan failure, `4` nothing parsed.

`--csv` appends one origin column at the end of the row: `MOD` for mod rows, the vanilla label otherwise.
Existing columns keep their index, and the row order already puts mod results above vanilla ones.

`_ModComponentTemp` is skipped by default: it holds the copies ModComponent unpacked, so scanning it
double-counts. Enable it with `--temp` or `ScanOptions.IncludeModComponentTemp`.

The console code page can be GBK, and a WinExe has no console handle at all, which makes
`Console.OutputEncoding` throw; `Cli` falls back to a UTF-8 writer so redirected output stays readable.

## 10. Design constraints

| # | Constraint | If violated | See |
| --- | --- | --- | --- |
| 1 | One read path per group; UI never re-filters | tab count and table row count diverge | §5 |
| 2 | Tools are never in an ingredient group | players think a hammer is consumed | README features |
| 3 | Blueprints and recipes keep separate product/ingredient groups | the same recipe appears twice and `TotalHits` double-counts | §5 |
| 4 | `RequiredTool == ""` is filtered before indexing | an empty ID enters the inverted table and matches every fuzzy query | §2.2 |
| 5 | `Ordinal` for definition stems, `OrdinalIgnoreCase` for queries | `GEAR_sugara` and `GEAR_SugarA` collapse into one item | §6 |
| 6 | Writes rewrite the whole archive, never patch in place | a crash mid-write leaves an unloadable container | §7.2 |
| 7 | Temp file is created next to the original and moved over it | a move across volumes is a copy, and the archive is briefly missing | §7.2 |
| 8 | The write only commits after the target entry was actually found | editing the wrong archive silently rewrites it unchanged | §7.2 |
| 9 | Existing BOM is preserved on write | the author's encoding habit changes under them | §7.3 |
| 10 | Missing parent directory entries are added on create | unpackers lose empty folders and the mod structure looks broken | §7.3 |
| 11 | Entries containing NUL are read-only | a texture is saved back as text and the bundle is corrupted | §7.3 |
| 12 | The index is rebuilt after every write | the UI shows "saved" while results stay stale | §3 |
| 13 | The left table is frozen while one row is edited | switching rows discards the unsaved buffer | §7.4 |
| 14 | Settings use `AppContext.BaseDirectory` | config files scatter into the shortcut's working directory | §4 |
| 15 | Parse failures degrade to a warning, never abort the scan | one malformed mod hides every other mod's blueprints | §2.4 |
| 16 | Vanilla rows are read-only: `IsVanilla` gates the edit buttons on top of the empty container path | the game's own recipes look editable, and the only possible write target is a mod's file | §2.5 |
| 17 | Vanilla is loaded even when the path holds no modcomponent | a wrong path silently hides data that does not depend on the path | §2.5 |
| 18 | Vanilla never shares a dedup key with a mod row, and sorts after it | the override relationship, the one thing the row is for, disappears | §5 |
| 19 | The strip and page frame are repainted after `WM_PAINT`, before any layout change is considered | a light frame stays around the results in the dark scheme, which is the one thing the night mode is for | §8.1 |
| 20 | Overpaint returns before painting when the scheme is light | the light look stops being the original look | §8.1 |

## 11. Comment migration map

Translating the code comments into English also moved every measured number and rationale into this file.
Nothing was dropped; the source keeps a conclusion plus a pointer.

| Old location | Old content | Now at |
| --- | --- | --- |
| `BlueprintParser.cs` type header | two entry layouts, per-field occurrence counts, old vs new gunpowder field | §2.1, §2.2 |
| `BlueprintParser.cs` `ParseGearRequirements` | three `RequiredGear` shapes and the 25 weighted occurrences | §2.3 |
| `ItemDefinitionParser.cs` type header | component counts, no-prefab-ID problem, trailing commas | §2.4 |
| `ModScanner.cs` type header | "a .modcomponent is a ZIP", reference to the extractor implementation | §2, §1 |
| `ItemDefinitionIndex.cs` type header | inference tiers and the `sugara` / `SugarA` story | §6 |
| `ModComponentArchive.cs` type header | why the archive is rewritten instead of patched | §7.2 |
| `ModComponentArchive.cs` `EnsureParentFolders` | why directory entries are restored | §7.3 |
| `ContainerEditorForm.cs` `TryLeaveCurrent` | the three gates and what cancel must do | §7.4 |
| `MainForm.cs` `OnEditClick` | why the table is frozen instead of a save prompt | §7.4 |
| `MainForm.cs` settings block | `BaseDirectory` vs `CurrentDirectory` | §4 |
| `BlueprintIndex.cs` type header | why inverted tables exist at this data size | §5 |
| `BlueprintSearchEngine.cs` type header | why the tool group is not split by kind | §5, intro |

## 12. Maintenance conventions

Every change has a fixed set of places to update. Missing one is how a repo starts lying to itself.

| Changed | Also update |
| --- | --- |
| A search rule or a match field | §5 here + the group table in the README (both language blocks) |
| A setting field, default or range | §8 here + the README table (both language blocks) |
| A write path, gate or order | §7 here + §10 row |
| Files added, removed or re-roled | §1 file map |
| Anything removed from a comment | §11 migration map, one row per move |
| Player-visible behaviour | README feature bullets (both blocks) |
| Version number | `csproj` `<Version>` -> README badge -> header of this file, all three equal |
| Game update | re-run `tools/export_vanilla.py`, rebuild (§2.5); the manifest's build string is what the UI reports |

Language split: comments and everything under `docs/` are English; only `README.md` carries Chinese.

## 13. Build and deploy

Requires Windows 10/11 and the .NET 8 SDK. No NuGet packages.

```bat
cd TLD_ModBlueprintFinder\TLD_ModBlueprintFinder
dotnet build -c Release
```

The repository root and the project folder share a name, so the command must run from the inner folder; from
Visual Studio, open `TLD_ModBlueprintFinder.sln` at the repository root.

**`bin\` output is not distributable.** It is framework-dependent and needs
`TLD_ModBlueprintFinder.dll`, `.deps.json` and `.runtimeconfig.json` beside it. Copying the exe alone gives a
silent no-op or a missing-.NET error. Shipping goes through the publish profile:

```bat
publish.bat
:: equivalent: dotnet publish TLD_ModBlueprintFinder -p:PublishProfile=SelfContainedSingleFile
```

| Setting | Value | Why |
| --- | --- | --- |
| `RuntimeIdentifier` | `win-x64` | single-file needs a RID; x86/ARM need their own build |
| `SelfContained` | `true` | the runtime is packed in - this is what makes "double-click anywhere" work |
| `PublishSingleFile` | `true` | managed dlls and runtime merged into one exe |
| `IncludeNativeLibrariesForSelfExtract` | `true` | native libraries packed too, extracted to `%TEMP%\.net\` on first run |
| `EnableCompressionInSingleFile` | `true` | ~100 MB -> ~65 MB, at the cost of about a second of startup |
| `PublishTrimmed` | `false` | WinForms does not support trimming; reflection breaks at runtime |
| `DebugType` | `none` | no pdb, so `dist\` holds exactly one file |

Artifact: `dist\TLD_ModBlueprintFinder.exe`. Verified by copying that single file into an empty directory whose
path contains spaces and Chinese characters: `--cli --scan-only` prints normally, the GUI starts, and it writes
its settings file next to itself.

`PublishSingleFile` only applies to `publish`; `build` always emits a folder of files. Do not ship from `bin\`.

## 14. Verification status

What has actually been checked, and how far:

| Area | Status |
| --- | --- |
| Build | `dotnet build -c Release` and `-c Debug`, 0 warnings, 0 errors |
| Scan | 45 containers, 414 mod blueprint records (15 recipes) + 133 vanilla records (104 blueprints, 29 recipes), 688 definitions, 480 item IDs, ~156 ms |
| Search | Baselines below, reproduced on 2.55 |
| Vanilla export | `tools/export_vanilla.py`: 533 bundles / 1431 CABs indexed in ~51 s, 104 blueprints + 29 recipes, 0 failures, 60 KB zip; row values cross-checked against the crafting screen (broth 1.0 kg meat + 1.5 L water) |
| Vanilla read-only | GUI probe on a vanilla-only hit (`OBJ_Rustic_Stool_Prefab`): the edit / save / back-up buttons all report disabled and the detail panel starts with the read-only notice; the same probe on a mod hit (`GEAR_Hammer`) has edit and back-up enabled |
| Colour schemes | GUI probe: ticking the night-mode checkbox repaints the main window and writes `"DarkMode": true`; a relaunch comes up dark (title bar attribute reads 1); unticking restores light, and the result diffs against a fresh light start in text antialiasing only - no control keeps a dark colour. Container editor captured in dark: list header, editor pane and buttons all follow |
| Write path | Console stub over real container copies: 38 cases (create / update / rename / delete / binary / path validation / folder containers) |
| Container editor | End-to-end in a sandbox copy: create -> edit -> rename -> delete, archive re-read and `testzip()` after each step |
| Single-file publish | CLI and GUI smoke test from `dist\`, including the embedded vanilla zip |

Baseline queries (expected output with the 45-container corpus plus the 2.55 snapshot):

| Command | Expect |
| --- | --- |
| `--item GEAR_ScrapMetal` | product 3, ingredient 44 (28 mod + 16 vanilla, all vanilla at the tail), tool 0, definitions 1 (contains-match on `knifescrapmetal`) |
| `--item GEAR_Hammer` | product 1, ingredient 0, tool 33 (26 mod + 7 vanilla), definitions 2 (`blacksmithhammer`, `stonehammer`) |
| `--item GEAR_sugara` | 5 recipe hits (2 products + 3 ingredients), 1 definition (`MoreMeds :: auto-mapped/sugara.json`), basis "same-mod reference" |
| `--item GEAR_SugarA` | same definition, plus a "different case" mark - vanilla sugar has no JSON of its own |
| `--item GEAR_Bones` | ingredient 3 (2 blueprints + 1 recipe), definitions 3 (`bones`, `orcabones1`, `orcabones2`) |
| `--item GEAR_1firconefuel` | 0 recipe hits, 2 definitions, basis "file name only" - definitions without recipes are normal |
| `--item GEAR_UncookedBroth` | group 4 has 2 rows: the mod's `recipes/bonebroth.json` (ingredients `POWDER_Salt 0.01 kg` / `LIQUID_WaterPotable 1.5 L`) and vanilla `recipes/dish_broth_a.json` after it |
| `--item GEAR_RawMeatRabbit` | group 2 has 3 rows, group 5 has 6 (1 mod + 5 vanilla dishes) - the groups do not overlap |
| `--item GEAR_RawMeatBear` | group 2 has 3 rows, group 5 has 2 (`REC_BearStew` + vanilla `dish_piepredator_a`) |
| `--item GEAR_Acorn --mode exact` | product 1, ingredient 4 (2 mod + vanilla `gear_acornshelled_a` and `gear_fishinglurec_a`) |
| `--item GEAR_Acorn --mode fuzzy` | product 7 (the exact row is excluded by the mode itself) |
| `--item GEAR_CougarWrap` | exactly 1 row, `BLUEPRINT_GEAR_CougarWrap` - the vanilla blueprint, not the 45 furniture ones |
| `--item OBJ_Rustic_Stool_Prefab` | 1 row: `BLUEPRINT_DECO_Stool`, a decoration product, read-only |
| `--no-vanilla --item GEAR_RawMeatRabbit` | group 5 drops to 1 row |

Self-check in four steps:

1. `--scan-only` reports hundreds of blueprint records and a comparable number of definitions. Zero means the
   `Mods` folder holds no `*.modcomponent`. The vanilla line (the one with the game build) must still be non-zero - it does not depend on
   the path, so a zero there means the embedded resource was not packaged.
2. `--item GEAR_Hammer` group 3 is not zero, otherwise only part of the mods were indexed.
3. Any row's raw JSON `CraftedResult` matches the "product" column - direct evidence that the field map is right.
4. Any group 6 row shows only `Mod*Component` / `Mod*Behaviour` top-level keys. `Name` / `CraftedResult` there
   means `IsDefinitionEntry` misclassified a recipe.

Known boundaries:

- **Vanilla covers blueprints and recipes, not item definitions.** The snapshot holds 104 craftables and 29
  dishes; group 6 still only answers for `auto-mapped/` files, because per-item prefab components are not part
  of the blueprint assets and exporting them would need a second, much larger pass.
- **A vanilla decoration product is a prefab name** (`OBJ_Rustic_Stool_Prefab`, `CONTAINER_Rustic_Closet_C`),
  not a `GEAR_` ID: furniture is placed, not carried, and the asset has no item ID to report.
- **Read-only by default.** Scanning, searching and exporting never touch `Mods`; writes only happen after an
  explicit button and are listed in §4.
- **DLL-only mods do not appear.** They ship no modcomponent.
- **Case-insensitive recipe search** treats `GEAR_SugarA` and `GEAR_sugara` as one query, which is a deliberate
  trade for hand-typed input. Definition lookup is not, see §6.
