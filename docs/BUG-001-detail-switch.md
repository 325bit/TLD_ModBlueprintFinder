# BUG-001 - Detail panel stops updating after switching back to "as product"

| Field | Value |
| --- | --- |
| ID | BUG-001 |
| Reported | 2026-10-25 |
| Area | UI layer, right-hand "detail / raw JSON" panel (shared by every tab) |
| Severity | Medium - no crash, no data loss, but a core path silently does nothing |
| Reproduction rate | 100% (`GEAR_SugarA` has exactly one row in "as product", selected from the start) |
| Status | Reproduced -> root-caused -> fixed -> regression-tested (fixed in 1.0.1; the executable was renamed `TLD_ModBlueprintFinder.exe` in 1.1.0) |

---

## 1. Summary

**This was neither a data problem nor a search problem. The detail panel had no idea which row it belonged to.**

The UI kept "which row should the detail panel show" in an implicit state: *which cell was clicked last*.
The panel is shared by every tab, no code resynced it when the tab changed, and clicking a row that was
already selected fires no selection event. Both paths were broken at once, so the panel stayed on the other
tab's row.

> This report describes the 1.0.0 build. Later versions added group 4/5 tabs and the definition tab, but the
> panel is still one shared control driven by the same `IDetailRow` sync path - the conclusion stands.
> Root cause and fix are summarised in `DEVELOPMENT.md` §7.4 and §10.

---

## 2. Environment

| Item | Value |
| --- | --- |
| Build | `bin/Release/net8.0-windows/win-x64/publish/TLD_ModBlueprintFinder.exe` |
| OS / DPI | Windows 10/11, 125% scaling, window 1396x899 |
| Game folder | a Steam install of The Long Dark |
| Index size | 46 modcomponents / 428 blueprints / 489 item IDs (139 ms) |
| Method | real mouse clicks (Win32 `SetCursorPos` + `mouse_event`), not programmatic selection |

---

## 3. Reproduction

Coordinates are window-absolute and replay exactly.

| # | Action | Observation |
| --- | --- | --- |
| 1 | Start and search `GEAR_SugarA` | status bar: 5 hits (1 product / 4 ingredients / 0 tools) |
| 2 | nothing | stays on "as product", the single `sugara` row is selected, detail shows sugara |
| 3 | click the "as ingredient" tab (414,262) | 4 rows appear (beerbottle / RabbitRiverSallad / Rosehip Jam / yeast), detail becomes beerbottle |
| 4 | click the `RabbitRiverSallad` row (300,339) | detail switches to RabbitRiverSallad - correct |
| 5 | click back to "as product" (319,262) | only `sugara` is listed and **still highlighted**, but the detail panel **still shows RabbitRiverSallad** |
| 6 | click the `sugara` row (300,315) | **nothing changes**, still RabbitRiverSallad |

Step 1-2, initial state:

![initial state](img/bug-1-initial.png)

Step 4, detail switched to RabbitRiverSallad:

![switched](img/bug-2-switched.png)

Step 5, tab switched back, sugara highlighted on the left, RabbitRiverSallad on the right:

![stale after tab switch](img/bug-3-stale-after-tab-switch.png)

Step 6, clicking the row again produces a screenshot **byte-identical** to step 5 (`bug-3` and `bug-4` have
the same MD5), proving the click had no effect at all:

![click has no effect](img/bug-4-click-noop.png)

---

## 4. Expected vs actual

| Step | Expected | Actual | Verdict |
| --- | --- | --- | --- |
| First render after search | sugara detail | sugara detail | pass |
| Switch tab, click another blueprint | detail follows the clicked row | follows | pass |
| Switch back to "as product" | detail shows the selected row of that tab (sugara) | previous tab's RabbitRiverSallad | fail |
| Click the sugara row | detail returns to sugara | no change | fail (what the user reported) |
| Any error surfaced | - | no message, no exception, no log | the defect is entirely silent |

> The user's "the click does nothing" has two layers, and both are needed to explain the experience:
> **the panel was already wrong** when the tab changed, and **clicking could not repair it** because clicking
> an already-selected row fires no event.

---

## 5. Trigger conditions

All three must hold:

1. The target item has results in **two different tabs**, and the user visits a non-default tab first -
   for `GEAR_SugarA`: one product plus four ingredients.
2. Coming back, the target row is **still selected** (never cleared).
3. The click lands on **the same row** (`SelectFirstRow` already made row 0 the current row after the search).

Condition 3 is why the bug looks intermittent: clicking **another column** of the same row changes
`CurrentCell`, which drags a selection event along and refreshes the panel. Measured: column 0 (blueprint
name) does nothing, column 3 (ingredient list) works.

---

## 6. Root cause

### 6.1 Event layer (direct cause): only `SelectionChanged` was wired

```csharp
// Ui/MainForm.Ui.cs, before the fix
grid.SelectionChanged += onSelectionChanged;   // the only subscription
```

`DataGridView.SelectionChanged` fires when the **selection set** changes. Clicking a row that is already
selected, on the current cell, changes nothing in that set, so the event never fires.

Probe log recorded on the same path:

```
13:47:55.966  ShowDetail(gridIngredient)  <- from _gridIngredient_SelectionChanged
13:47:55.980  SelectionChanged row=1
13:47:56.025  CellClick row=1 col=0       <- normal: selection change and click both arrive

13:47:57.559  CellClick row=0 col=0       <- user clicks sugara: click only, no SelectionChanged
```

The last line is the defect: `CellClick` arrived with no listener, `SelectionChanged` never came.

### 6.2 State layer (root cause): the shared control had no "owning tab" state

`_txtDetail` is one box shared by every tab, and nothing recorded which tab and which row it was showing. The
rendering code answered "which row was clicked" but never "which row should be shown".

On step 5 there was therefore no opportunity to correct anything: `SelectedIndexChanged` had no handler. Even
without clicking, the window was already self-contradictory - sugara selected on the left, RabbitRiverSallad
written on the right.

### 6.3 Data identity layer (checked, not the cause)

Each exclusion demanded by the diagnosis was verified:

- Every grid row binds its own `SearchRow` instance carrying its own `BlueprintRecord`; the text comes from
  `row.BuildDetailText()`, with no global "current blueprint" variable involved.
- The `GEAR_sugara` hits in "as product" and "as ingredient" belong to two different records (`sugara` and
  `RabbitRiverSallad`), and the index builds `CraftedResult` and `RequiredGear[].Item` as separate tables.
- Conclusion: **no identity mismatch**. Once the missing events were added, the same row always produced the
  same text, which the regression run confirmed.

---

## 7. Fix

Three changes: close the event gap, add the missing state, and give an empty tab something explicit to say.

### 7.1 Add a click event (`Ui/MainForm.Ui.cs`)

```csharp
// Both events are required:
//   SelectionChanged - arrow keys and programmatic selection (the selection really changed)
//   CellClick        - mouse click on the already-selected row (selection unchanged, no event)
grid.SelectionChanged += onSelectionChanged;
grid.CellClick += (_, e) => { if (e.RowIndex >= 0) ShowDetail(grid); };
```

`CellClick` fires on mouse-up regardless of selection changes, which is exactly the blind spot. Column headers
(`RowIndex = -1`) are filtered out.

### 7.2 Resync on tab change (`Ui/MainForm.cs`, subscribed in `Ui/MainForm.Ui.cs`)

```csharp
private void OnTabChanged(object? sender, EventArgs e)
{
    var grid = ActiveGrid();          // the grid belonging to the current tab

    if (grid.Rows.Count == 0)
    {
        if (_outcome is { TotalHits: > 0 })
            SetDetailText(EmptyTabHint);   // "no matching results in this tab, switch back" (UI text is Chinese)
        return;
    }

    if (grid.CurrentRow is null)      // a tab shown for the first time may have no current row
    {
        grid.ClearSelection();
        grid.Rows[0].Selected = true;
        grid.CurrentCell = grid.Rows[0].Cells[0];
    }

    ShowDetail(grid);
}
```

This also fixes a sibling problem: switching to an **empty tab** used to keep showing another tab's blueprint,
which reads as content of that tab. It now says "no matching results".

### 7.3 Skip identical renders (`Ui/MainForm.cs`)

```csharp
var text = row.BuildDetailText();
if (_txtDetail.Text == text) return;   // already showing this row
```

Selection change and mouse click both trigger a render; without the guard the scroll position is reset twice
(`SelectFirstRow` used to render three times in a row after a search), which is painful when reading long
JSON. It also makes repeated clicks on the same row idempotent - which is what "the click does nothing"
should mean.

### 7.4 Related hardening

- `ShowDetail` falls back to the first selected row when `CurrentRow` is null (Ctrl multi-select, programmatic
  selection).
- The three grids got `Name`s (`gridProduct` / `gridIngredient` / `gridTool`), so any stack trace or log line
  carries context.

---

## 8. Regression

Replayed on the same machine and data set, plus edge cases.

| # | Case | Expected | Observed | Result |
| --- | --- | --- | --- | --- |
| R1 | `GEAR_SugarA` -> ingredient tab -> click `RabbitRiverSallad` | detail = RabbitRiverSallad | matches | pass |
| R2 | switch back to product tab, click nothing | detail = sugara | matches | pass |
| R3 | click the sugara row again | unchanged | screenshot byte-identical to R2 | pass (idempotent) |
| R4 | click another **column** of the same row | detail = sugara | matches | pass |
| R5 | switch to the empty tool tab | explicit "no matching results" | matches | pass |
| R6 | from tool tab back to ingredient tab | detail = selected row (beerbottle) | matches | pass |
| R7 | retype `GEAR_Cloth` and press Enter | jumps to ingredient tab, detail = first row | matches | pass |
| R8 | `GEAR_Cloth`, open the empty product tab | "no matching results" | matches | pass |
| R9 | in a 42-row list press Down twice | detail follows to row 3 | matches | pass |
| R10 | Ctrl+click row 4 | detail = that row (yeast) | matches | pass |
| R11 | CLI: `GEAR_SugarA` / `ScrapMetal` / `Hammer` / `UncookedBroth` | 1-4-0 / 3-28-0 / 1-0-26 / 1-0-0 | identical | pass |

R2 after the fix - selected sugara on the left, sugara in the detail panel:

![detail restored](img/fix-1-detail-restored.png)

R5, the explicit empty-tab hint:

![empty tab hint](img/fix-2-empty-tab-hint.png)

> Note: `bug-1-initial.png` and `fix-1-detail-restored.png` are byte-identical, which is the point of the fix:
> coming back to a tab must look exactly like the moment the search finished.

---

## 9. Blast radius

Every operation reachable through the same root cause was checked:

| Scenario | Affected | Note |
| --- | --- | --- |
| Click column 0 of an already-selected row | yes | the reported defect; fixed |
| Click another column of the same row | no | `CurrentCell` change drags a selection event; still fine after the fix |
| Switch tabs without clicking | yes | detail stayed on the old tab; fixed by 7.2 |
| Arrow keys | no | selection really changes; covered by R9 |
| Ctrl multi-select | no | `CurrentRow` follows the clicked cell; covered by R10 |
| After a new search | no | `SelectFirstRow` resets the current row; covered by R7 |
| CSV export / copy detail | no | reads `row` and `_txtDetail.Text`, not "current selection" |

`Parsing/` and `Indexing/` were untouched, and CLI output is identical to the pre-fix build (R11).

---

## 10. Unverified hypotheses

Neither of these has evidence, and neither should be quoted as a conclusion:

1. *Hypothesis*: if a mod's `CraftedResult` and `RequiredGear[].Item` point at the same ID within one
   modcomponent, both tabs would show a row of the same name, and the panel might still be ambiguous after a
   tab switch. Verifying it needs such a mod; no sample exists in the current corpus.
2. *Hypothesis*: with very large result sets (thousands of rows) the whole-string comparison
   `_txtDetail.Text == text` grows with the text. At today's 6 KB details and 428 records it is not
   measurable; if the index ever reaches five digits, compare row references instead.

---

## 11. Deliverables

- Source fix: `TLD_ModBlueprintFinder/Ui/MainForm.cs` (render, tab sync), `TLD_ModBlueprintFinder/Ui/MainForm.Ui.cs`
  (both events, grid names)
- Updated binary: the published single-file build in `dist\`
- Reproduction and verification screenshots: `docs/img/`
- Reproduction was driven by a Win32 click script (`SetCursorPos` + `mouse_event`) kept outside the repository;
  the screenshots above are its output.
