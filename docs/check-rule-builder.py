#!/usr/bin/env python3
# Lists the public fields of AbilityAIExtension that docs/rule-builder.html doesn't offer, and the builder's fields
# that no longer exist in the C#. Run from the mod's root before a release: python3 docs/check-rule-builder.py
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
src = (root / "Source/AbilityAI/AbilityAI.cs").read_text(encoding="utf-8")
start = src.index("public class AbilityAIExtension")
end = src.index("[Unsaved] private AbilityAIWorker worker", start)
fields = set(re.findall(r"public\s+[\w<>?.]+\s+(\w+)\s*(?:=[^;]*)?;", src[start:end]))
# workerClass, npc and friendlyNpc are drawn by the builder itself; setting names only Ability AI's own switches
fields -= {"workerClass", "npc", "friendlyNpc", "setting"}

page = (root / "docs/rule-builder.html").read_text(encoding="utf-8")
offered = set(re.findall(r'\{ n: "(\w+)"', page))

missing = sorted(fields - offered)
stale = sorted(offered - fields)
for n in missing:
    print(f"not in the builder: {n}")
for n in stale:
    print(f"in the builder but not in AbilityAIExtension: {n}")
if not missing and not stale:
    print(f"rule builder matches AbilityAIExtension ({len(fields)} fields)")
sys.exit(1 if missing or stale else 0)
