# BUG-002 - Switching entries in the container editor asks to save untouched files

| Field | Value |
| --- | --- |
| ID | BUG-002 |
| Reported | 2026-11-09 |
| Area | Container editor (`Ui/ContainerEditorForm.cs`), entry baseline / dirty flag |
| Severity | Medium - no data loss, but 75% of entries look edited and the prompt can silently rewrite the archive |
| Reproduction rate | 100% for any entry whose JSON uses CRLF line endings |
| Status | Reproduced -> root-caused -> fixed -> regression-tested (fixed in 1.5.1) |

---

## 1. Summary

**Nothing was triggering the prompt except the load itself.** The editor decided "this entry has unsaved
changes" by comparing the editor's current text with a baseline captured from the file, and those two
strings could never be equal for a CRLF file:

```csharp
_original = content.Text;        // file text as stored: "...{\r\n..."
_editor.Text = content.Text;
...
_dirty = _current is not null && !_editor.ReadOnly && _editor.Text != _original;
```

`RichTextBox.Text` reports every line break as LF. The control stores CRLF internally (`WM_GETTEXT`
returns 29 CR and 29 LF for a 29-line file), but the managed property folds it. So a freshly loaded CRLF
entry differed from its own baseline by one `\r` per line: `_dirty` was true before the user touched a key.

The prompt only surfaced on the *next* selection, because `TryLeaveCurrent()` is called from
`OnListSelectionChanged`. That is also why the dialog named the file the user was leaving, not the one being
opened - it was never about the click.

## 2. Environment

- Game 2.55, `AHandyToolbox.modcomponent` and 45 others in `Mods\`
- Windows 11, .NET 8, WinForms `RichTextBox`
- 1.5.0 build

## 3. Reproduction

1. Open the container editor on any container.
2. Do not type anything. The header already reads `● blueprints/cantometal_blueprint.json` and both
   the save and revert buttons are enabled.
3. Click any other entry: the "unsaved changes - save first?" dialog appears, naming the entry that was
   already open.

## 4. Evidence

Measured on the same control at the same moment, entry `auto-mapped/battery9v.json` (1691 bytes, 29 CRLF):

| Read path | CR | LF | Equals the file |
| --- | --- | --- | --- |
| `WM_GETTEXT` (native) | 29 | 29 | yes |
| `RichTextBox.Text` (managed) | 0 | 29 | **no** |
| `RichTextBox.Text` after loading the same content with LF only | 0 | 29 | yes |

A control without a window handle does not fold line breaks, so a probe must `Show()` the form and pump
messages first; otherwise it reports the opposite and the bug looks unreproducible.

Scale on the reference install: 1173 JSON entries across 46 containers - 886 CRLF (75.5%), 279 LF,
8 mixed. Every CRLF entry hit the bug.

## 5. Why it was not caught earlier

The new-file templates are C# raw string literals from an LF source file, and the first manual passes used
those templates. Only third-party entries are CRLF.

## 6. Side effect worth knowing

`SaveCurrent()` guards against a no-op write with `if (text == _original)`, which used the same broken
comparison. Answering "Yes" therefore rewrote the whole archive for an edit that never happened, and the
file came back with LF line endings. After that rewrite the baseline matched, so the prompt disappeared -
the bug looked self-healing.

## 7. Fix

Capture the baseline from the control after the text is in it, so the comparison is between two strings
produced by the same control:

```csharp
_editor.Text = content.Text;
...
_original = _editor.Text;   // not content.Text
```

The same one-line change went into `MainForm.BeginEdit` (`EditSession.OriginalText`), where the identical
comparison decides whether an in-place edit is a no-op.

Line endings still change on a real save: the write path stores what the editor holds, which is LF. This
fix does not address that and does not pretend to - preserving the original style is a separate decision.

## 8. Regression

`analysis/regress_editor_dirty.py` and `analysis/regress_inline_edit.py` (Python + Win32 messages):

1. Open the editor: no bullet, save and revert disabled.
2. Type one character: both enabled, bullet appears.
3. Close with unsaved changes: prompt appears; "No" leaves the archive byte-identical and closes.
4. Main window: edit, then save with no typing - archive size and mtime unchanged, no backup
   written.
5. Sandbox copy, one character typed then saved: exactly one entry rewritten, still valid JSON, backup
   created, `ZipFile.testzip()` clean.

Note: driving the `ListView` with synthetic `WM_LBUTTONDOWN` crashes the process (`0xC0000005`), so the
"leave" path is exercised through `WM_CLOSE` instead - it reaches the same `TryLeaveCurrent()`.
