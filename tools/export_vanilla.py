"""Dump the game's own blueprints out of the Addressables bundles into the modcomponent JSON shape.

The tool reads nothing but JSON, so vanilla data is exported once here and shipped inside the exe.
Re-run this after a game update:

    python tools/export_vanilla.py --game "F:/GameAbouts/The Long Dark/TheLongDark_2.55"

Output (all paths relative to the repository root):
    data/vanilla/blueprints/*.json   one file per craftable, ModComponent blueprint shape
    data/vanilla/recipes/*.json      cooking dishes, ModComponent recipe shape
    data/vanilla/manifest.json       game version, counts, export time
    data/vanilla.zip                 the same tree, embedded into the exe

Why the bundles and not a text file: the game keeps `BlueprintData` as ScriptableObjects inside
Addressables bundles (assets/ScriptableObjects/Gear/Blueprints/BLUEPRINT_*.asset). UnityPy can read
their type trees, but every item/tool/product field is a PPtr into another bundle, so an export has to
build a CAB index over the whole game folder first (about a minute) and then resolve two levels
(component -> GameObject name).

Two field traps, both silent - the export succeeds and the numbers look right:

  * `m_CraftingResultType` decides which product field is real. CraftingResult.StandardGear (0) uses
    `m_CraftedResultGear`; CraftingResult.Decoration (1) uses `m_CraftedResultDecoration`, and the gear
    pointer still holds a stale prefab (all 45 furniture blueprints pointed at GEAR_CougarWrap).
  * `RequiredGearItem.m_Units` picks count vs weight: Units.Kilograms (1) means `m_Count` is unused and
    `m_Weight.m_Units` carries the amount, so those lines must be emitted as Quantity + Units.
"""

import argparse
import glob
import json
import os
import sys
import time
import zipfile

import UnityPy

BLUEPRINT_CABS = "Assets/ScriptableObjects/Gear/Blueprints"

# CraftingLocation / SkillType, in enum order (dump.cs, 2.55)
CRAFTING_LOCATION = ["Anywhere", "Workbench", "Forge", "AmmoWorkbench", "Fire", "FurnitureWorkbench"]
SKILL = ["Firestarting", "CarcassHarvesting", "IceFishing", "Cooking", "Rifle", "Archery",
         "ClothingRepair", "ToolRepair", "Revolver", "Gunsmithing"]

# CraftingResult: which product pointer is meaningful
RESULT_STANDARD_GEAR = 0
RESULT_DECORATION = 1

# RequiredGearItem.Units
UNITS_COUNT = 0
UNITS_KILOGRAMS = 1

# ItemWeight.m_Units / ItemLiquidVolume.m_Units are fixed point at 1e9 units per kg / per litre.
# Evidence: ItemLiquidVolume..cctor writes Liter = 0x3B9ACA00 (1e9) and Centiliter = 0x989680 (1e7);
# the weights agree with values a player can check (broth 1.0 kg meat, meat pie 0.5 kg meat + 0.2 kg
# flour, cooking oil 1.0 kg fat). Using 1e8 here multiplies every requirement by ten, which still
# looks plausible per row - that is why this needs the literal, not a guess.
UNITS_PER_KG = 1_000_000_000.0


def location_name(v):
    return CRAFTING_LOCATION[v] if 0 <= v < len(CRAFTING_LOCATION) else "Anywhere"


def skill_name(v):
    return "None" if v < 0 else (SKILL[v] if v < len(SKILL) else "None")


def bundle_paths(game):
    base = os.path.join(game, r"tld_Data\StreamingAssets\aa\StandaloneWindows64")
    dlc = os.path.join(game, r"tld_dlc")
    out = sorted(glob.glob(os.path.join(base, "*.bundle")))
    out += sorted(glob.glob(os.path.join(dlc, "**", "*.bundle"), recursive=True))
    return base, out


def scan_bundles(bundles, cache_path):
    """One pass over every bundle: which CAB lives where, and which bundles hold BLUEPRINT_* assets."""
    if os.path.exists(cache_path):
        with open(cache_path, encoding="utf-8") as fp:
            data = json.load(fp)
        if data.get("bundle_count") == len(bundles):
            print("bundle scan: cached (%d cabs, %d blueprint bundles)"
                  % (len(data["cabs"]), len(data["blueprintBundles"])))
            return data["cabs"], data["blueprintBundles"]

    index = {}
    bp_bundles = []
    t0 = time.time()
    for i, p in enumerate(bundles, 1):
        try:
            env = UnityPy.load(p)
        except Exception:
            continue
        for bf in env.files.values():
            for cab in bf.files.keys():
                index.setdefault(cab, p)
        try:
            if any(str(obj.peek_name()).startswith("BLUEPRINT_") for obj in env.objects
                   if obj.type.name == "MonoBehaviour"):
                bp_bundles.append(p)
        except Exception:
            pass
        if i % 100 == 0:
            print("  scanned %d/%d bundles (%.0fs)" % (i, len(bundles), time.time() - t0))

    print("bundle scan: %d cabs, %d blueprint bundles, %d bundles in %.0fs"
          % (len(index), len(bp_bundles), len(bundles), time.time() - t0))
    os.makedirs(os.path.dirname(cache_path), exist_ok=True)
    with open(cache_path, "w", encoding="utf-8", newline="\n") as fp:
        json.dump({"bundle_count": len(bundles), "cabs": index,
                   "blueprintBundles": bp_bundles}, fp)
    return index, bp_bundles


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=r"F:\GameAbouts\The Long Dark\TheLongDark_2.55")
    ap.add_argument("--out", default=None, help="repository data directory (default: <repo>/data)")
    ap.add_argument("--cache", default=None)
    args = ap.parse_args()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = args.out or os.path.join(repo, "data")
    cache = args.cache or os.path.join(repo, "tools", ".cache", "cab-index.json")

    base, bundles = bundle_paths(args.game)
    if not bundles:
        print("no bundles under", base)
        return 2

    index, bp_bundles = scan_bundles(bundles, cache)
    print("blueprint bundles:", len(bp_bundles))
    for p in bp_bundles:
        print("  ", os.path.basename(p))

    env = UnityPy.load(*bp_bundles)

    needed = set()
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            if not str(getattr(obj.read(), "m_Name", "")).startswith("BLUEPRINT_"):
                continue
        except Exception:
            continue
        for ext in obj.assets_file.externals:
            needed.add(ext.path.split("/")[1] if "/" in ext.path else ext.path)

    extra = sorted({index[c] for c in needed if c in index})
    print("blueprints reference %d cabs -> loading %d extra bundles" % (len(needed), len(extra)))
    for p in extra:
        if p not in bp_bundles:
            env.load_file(p)

    objs = {}
    for obj in env.objects:
        objs[(obj.assets_file.name, obj.path_id)] = obj
    print("objects loaded:", len(objs))

    def resolve(ptr, owner):
        fid, pid = ptr["m_FileID"], ptr["m_PathID"]
        cab = owner.assets_file.name if fid == 0 else owner.assets_file.externals[fid - 1].path.split("/")[1]
        return objs.get((cab, pid))

    def name_of(ptr, owner, depth=0):
        target = resolve(ptr, owner)
        if target is None or depth > 3:
            return None
        if target.type.name == "GameObject":
            return getattr(target.read(), "m_Name", None)
        try:
            tt = target.read_typetree()
        except Exception:
            return getattr(target.read(), "m_Name", None)
        go = tt.get("m_GameObject")
        if not go or not go.get("m_PathID"):
            return getattr(target.read(), "m_Name", None)
        return name_of(go, target, depth + 1)

    blueprints, recipes, failed = [], [], []
    decoration_results = 0
    weighted_gear = 0

    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            asset_name = str(getattr(obj.read(), "m_Name", ""))
            if not asset_name.startswith("BLUEPRINT_"):
                continue
            t = obj.read_typetree()
        except Exception as ex:
            failed.append((obj.path_id, repr(ex)))
            continue

        gear = []
        for g in t.get("m_RequiredGear") or []:
            nm = name_of(g["m_Item"], obj)
            if not nm:
                continue
            if g.get("m_Units", UNITS_COUNT) == UNITS_KILOGRAMS:
                kg = (g.get("m_Weight") or {}).get("m_Units", 0) / UNITS_PER_KG
                gear.append({"Item": nm, "Quantity": round(kg, 4), "Units": "Kilograms"})
            else:
                gear.append({"Item": nm, "Count": int(g.get("m_Count", 1))})

        powder = []
        for g in t.get("m_RequiredPowder") or []:
            nm = name_of(g["m_Powder"], obj)
            if nm:
                units = (g.get("m_Quantity") or {}).get("m_Units", 0)
                powder.append({"Powder": nm, "QuantityInKilograms": round(units / UNITS_PER_KG, 4)})

        liquid = []
        for g in t.get("m_RequiredLiquid") or []:
            nm = name_of(g["m_Liquid"], obj)
            if nm:
                units = (g.get("m_Volume") or {}).get("m_Units", 0)
                liquid.append({"Liquid": nm, "VolumeInLitres": round(units / UNITS_PER_KG, 4)})

        required_tool = ""
        tool = t.get("m_RequiredTool") or {}
        if tool.get("m_PathID"):
            required_tool = name_of(tool, obj) or ""

        optional = []
        for o in t.get("m_OptionalTools") or []:
            if o.get("m_PathID"):
                nm = name_of(o, obj)
                if nm:
                    optional.append(nm)

        # Which product pointer is real depends on the type: a decoration blueprint leaves a stale prefab
        # in m_CraftedResultGear, so reading it unconditionally invents a product that does not exist.
        if t.get("m_CraftingResultType", RESULT_STANDARD_GEAR) == RESULT_DECORATION:
            ptr = t.get("m_CraftedResultDecoration") or {}
            result_name = name_of(ptr, obj) if ptr.get("m_PathID") else ""
            decoration_results += 1
        else:
            ptr = t.get("m_CraftedResultGear") or {}
            result_name = name_of(ptr, obj) if ptr.get("m_PathID") else ""
        result_name = result_name or ""
        weighted_gear += sum(1 for g in t.get("m_RequiredGear") or []
                             if g.get("m_Units", UNITS_COUNT) == UNITS_KILOGRAMS)

        body = {
            "Name": asset_name,
            "RequiredGear": gear,
            # No KeroseneLitersRequired / GunpowderKGRequired: 2.55's BlueprintData has no such field, and
            # writing 0 would suggest the field exists and is simply empty. Fuel needs live in RequiredPowder.
            "RequiredTool": required_tool,
            "OptionalTools": optional,
            "RequiredCraftingLocation": location_name(t.get("m_RequiredCraftingLocation", 0)),
            "RequiresLitFire": bool(t.get("m_RequiresLitFire", 0)),
            "RequiresLight": bool(t.get("m_RequiresLight", 0)),
            "CraftedResult": result_name,
            "CraftedResultCount": int(t.get("m_CraftedResultCount", 1) or 1),
            "DurationMinutes": int(t.get("m_DurationMinutes", 0) or 0),
            "CraftingAudio": "",
            "AppliedSkill": skill_name(t.get("m_AppliedSkill", -1)),
            "ImprovedSkill": skill_name(t.get("m_ImprovedSkill", -1)),
        }
        if powder:
            body["RequiredPowder"] = powder
        if liquid:
            body["RequiredLiquid"] = liquid

        if asset_name.startswith("BLUEPRINT_DISH"):
            recipes.append({
                "RecipeName": asset_name,
                "RecipeDescription": "",
                "RecipeIcon": "",
                "RequiredSkillLevel": 0,
                "AllowedCookingPots": [required_tool] if required_tool else [],
                "BlueprintData": body,
            })
        else:
            blueprints.append(body)

    print("blueprints: %d (decoration results %d), recipes: %d, failed: %d, weighted gear lines: %d"
          % (len(blueprints), decoration_results, len(recipes), len(failed), weighted_gear))
    for f in failed[:5]:
        print("  fail:", f)

    nameless = [b["Name"] for b in blueprints if not b["CraftedResult"]]
    if nameless:
        print("  no product resolved (%d):" % len(nameless), nameless[:5])

    bp_dir = os.path.join(out_dir, "vanilla", "blueprints")
    rc_dir = os.path.join(out_dir, "vanilla", "recipes")
    for d in (bp_dir, rc_dir):
        os.makedirs(d, exist_ok=True)
        for old in glob.glob(os.path.join(d, "*.json")):
            os.remove(old)

    def file_stem(name):
        return name.replace("BLUEPRINT_", "").lower().replace("/", "_")

    for b in blueprints:
        with open(os.path.join(bp_dir, file_stem(b["Name"]) + ".json"), "w", encoding="utf-8", newline="\n") as fp:
            json.dump(b, fp, indent=2, ensure_ascii=False)
            fp.write("\n")

    for r in recipes:
        with open(os.path.join(rc_dir, file_stem(r["RecipeName"]) + ".json"), "w", encoding="utf-8", newline="\n") as fp:
            json.dump(r, fp, indent=2, ensure_ascii=False)
            fp.write("\n")

    version = read_version(args.game)
    manifest = {
        "game": "The Long Dark",
        "gameVersion": version,
        "exportedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "blueprints": len(blueprints),
        "recipes": len(recipes),
        "source": BLUEPRINT_CABS,
    }
    with open(os.path.join(out_dir, "vanilla", "manifest.json"), "w", encoding="utf-8", newline="\n") as fp:
        json.dump(manifest, fp, indent=2, ensure_ascii=False)
        fp.write("\n")

    zip_path = os.path.join(out_dir, "vanilla.zip")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(os.path.join(out_dir, "vanilla")):
            for f in sorted(files):
                full = os.path.join(root, f)
                arc = os.path.relpath(full, os.path.join(out_dir, "vanilla")).replace("\\", "/")
                z.write(full, "vanilla/" + arc)
    print("wrote", zip_path, "%.1f KB" % (os.path.getsize(zip_path) / 1024))
    print(json.dumps(manifest, ensure_ascii=False))
    return 0


def read_version(game):
    for candidate in (os.path.join(game, r"tld_Data\StreamingAssets\version.txt"),
                      os.path.join(game, "version.txt")):
        if os.path.exists(candidate):
            with open(candidate, encoding="utf-8", errors="replace") as fp:
                return fp.read().strip()
    return "unknown"


if __name__ == "__main__":
    sys.exit(main())
