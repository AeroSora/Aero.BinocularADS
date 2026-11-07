"""Check a release tree against SPEC-BinocularRelease criteria 1–4 and 8.

    python -I tools/release_check.py <tree>

    python -I tools/release_check.py --selftest

The self-test covers a missing root and clean and violating fixtures. A scan reports violations by file and line. Exit 0 means clean, 1 means a violation, and 2 means the directory does not exist. The development tree can also be scanned to see what a release needs to exclude.

The release tree excludes decompilation, ReferencesLive, evidence logs and captures, internal notes, legacy or developer-only sources, shader staging copies and experiments. Source, shader and build text must avoid other mod names or descriptions, legacy compile guards, personal and owner references, first-person narrative, chat quotes, dated history, process references and work-item IDs. Plugin.VERSION and the project <Version> must match in MAJOR.MINOR.PATCH format.
"""
import re
import sys
import tempfile
from pathlib import Path

SOURCE_SUFFIXES = {".cs", ".shader", ".cginc", ".hlsl", ".csproj", ".props", ".targets", ".ps1", ".py"}
ALLOWED_DOCS = {"README.md", "CHANGELOG.md", "LICENSE", "LICENSE.md"}

PATH_RULES = [
    (re.compile(r"(^|/)ReferencesLive(/|$)"), "private reference material (decompiled code, evidence)"),
    (re.compile(r"\.decompiled\.cs$"), "decompiled code"),
    (re.compile(r"(^|/)evidence[^/]*\.(log|txt)$|\.log$"), "evidence or log file"),
    (re.compile(r"\.(png|jpg|bmp)$"), "capture image"),
    (re.compile(r"(^|/)(FrontPostAnchor|AimLineAnchor)\.cs$"), "uncompiled legacy source"),
    (re.compile(r"\.Developer\.(cs|shader)$"), "developer-only source"),
    (re.compile(r"(^|/)Preflight/Unity/Assets/ProductionShaders/"), "tracked staging copy of Shaders/"),
    (re.compile(r"(^|/)Preflight/Unity/Assets/Shaders/"), "experiment shader"),
    (re.compile(r"(^|/)Preflight/IlGate/"), "development test harness"),
    (re.compile(r"(^|/)Preflight/Unity/Assets/Editor/AeroPreflightRunner\.cs$"), "development test harness"),
    (re.compile(r"(^|/)tools/(bundle_align_survey|marker_vs_mesh|provenance_overlap)\.py$|sight-align-survey"), "development survey tool"),
]

TEXT_RULES = [
    (re.compile(r"(?i)borkel|realisticnvg|realisticnightvision|transparentsights"), "names or describes another mod"),
    (re.compile(r"(?i)reference (mod|implementation|nvg|look)"), "describes another mod as the reference"),
    (re.compile(r"LEGACY_IRON_SIGHT_PICKERS"), "legacy compile guard"),
    (re.compile(r"\bMaster\b"), "personal name"),
    (re.compile(r"(?i)\bthe owner\b|\bowner's\b|\bowner (accepted|asked|reported|happened|was|changed)"), "owner reference"),
    (re.compile(r"(?i)\b(Kurumi|Sumire|nya|woof)\b"), "persona text"),
    (re.compile(r"(?i)\bI (previously|missed|recommend|tried|found|think)\b|\bmy \d|\bMy 0"), "first-person narrative"),
    (re.compile(r"\*\"[^\"]+\"\*"), "quoted chat line"),
    (re.compile(r"(?i)7bpencil|weaponcamoandstickers|amandsgraphics"), "names or describes another mod"),
    (re.compile(r"//.*\b20\d\d-\d\d-\d\d\b"), "dated development history in a comment"),
    (re.compile(r"//.*(\b[Aa]udit\b|AUDIT-|\bAmendment\b|\bJOURNAL\b|\b[Cc]ontract section\b)"), "development process reference in a comment"),
    (re.compile(r"//.*\b(F[0-9A-F]{6}|T1\d\d)\b"), "finding or work-item id in a comment"),
]


def doc_rule(rel):
    name = rel.rsplit("/", 1)[-1]
    if name.endswith(".md") and name not in ALLOWED_DOCS:
        return "internal note"
    return None


SEMVER = re.compile(r"^\d+\.\d+\.\d+$")


def version_problems(root):
    """Check that Plugin.VERSION and the project <Version> match in MAJOR.MINOR.PATCH format."""
    plugin = root / "Plugin.cs"
    projects = sorted(root.glob("*.csproj"))
    if not plugin.is_file() or not projects:
        return []
    declared = re.search(r'public const string VERSION = "([^"]*)"', plugin.read_text(encoding="utf-8", errors="replace"))
    built = re.search(r"<Version>([^<]*)</Version>", projects[0].read_text(encoding="utf-8", errors="replace"))
    if not declared or not built:
        return [("Plugin.cs", 0, "version declaration missing (Plugin.VERSION or <Version>)")]
    if declared.group(1) != built.group(1):
        return [("Plugin.cs", 0, f"version mismatch: Plugin.VERSION {declared.group(1)} vs <Version> {built.group(1)}")]
    if not SEMVER.match(declared.group(1)):
        return [("Plugin.cs", 0, f"version {declared.group(1)} is not MAJOR.MINOR.PATCH")]
    return []


def scan(root):
    problems = version_problems(root)
    for path in sorted(p for p in root.rglob("*") if p.is_file()):
        rel = path.relative_to(root).as_posix()
        if rel.startswith((".git/", "bin/", "obj/")) or "/.git/" in rel:
            continue
        for rule, why in PATH_RULES:
            if rule.search(rel):
                problems.append((rel, 0, why))
                break
        else:
            why = doc_rule(rel)
            if why:
                problems.append((rel, 0, why))
                continue
            if path.suffix.lower() in SOURCE_SUFFIXES and rel != "tools/release_check.py":
                for number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
                    for rule, why in TEXT_RULES:
                        if rule.search(line):
                            problems.append((rel, number, why))
    return problems


def check(target):
    root = Path(target).resolve()
    if not root.is_dir():
        print(f"release check: {target} is not an existing directory")
        return 2
    problems = scan(root)
    for rel, number, why in problems:
        print(f"{rel}:{number}: {why}" if number else f"{rel}: {why}")
    kinds = {}
    for _, _, why in problems:
        kinds[why] = kinds.get(why, 0) + 1
    print(f"release check: {len(problems)} violation(s)" + (": " + ", ".join(f"{k} {v}" for k, v in sorted(kinds.items())) if kinds else ""))
    return 1 if problems else 0


def selftest():
    results = []
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)
        clean = base / "clean"
        (clean / "Shaders").mkdir(parents=True)
        nl = chr(10)
        (clean / "Plugin.cs").write_text("// Applies the defocus blur to the weapon images." + nl +
                                         'class Plugin { public const string VERSION = "0.1.0"; }' + nl, encoding="utf-8")
        (clean / "Mod.csproj").write_text("<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>" + nl,
                                          encoding="utf-8")
        (clean / "Shaders" / "Blur.shader").write_text("// Separable Gaussian." + nl, encoding="utf-8")
        (clean / "README.md").write_text("# Mod" + nl, encoding="utf-8")
        dirty = base / "dirty"
        (dirty / "ReferencesLive").mkdir(parents=True)
        (dirty / "ReferencesLive" / "EFT.Player.decompiled.cs").write_text("class Player {}" + nl, encoding="utf-8")
        (dirty / "NOTES.md").write_text("notes" + nl, encoding="utf-8")
        (dirty / "Plugin.Developer.cs").write_text("partial class Plugin {}" + nl, encoding="utf-8")
        (dirty / "Plugin.cs").write_text(nl.join(["// Borkel's formula.", "#if LEGACY_IRON_SIGHT_PICKERS",
                                                  "// Master's value.", "#endif",
                                                  "// Measured 2026-11-05 (audit C2, F78F063).",
                                                  'public const string VERSION = "1.2.4";', ""]), encoding="utf-8")
        (dirty / "Mod.csproj").write_text("<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>" + nl,
                                          encoding="utf-8")
        cases = [("missing root is refused (exit 2)", check(str(base / "missing")), 2),
                 ("a file is refused as a root (exit 2)", check(str(clean / "Plugin.cs")), 2),
                 ("clean fixture passes (exit 0)", check(str(clean)), 0),
                 ("violating fixture fails (exit 1)", check(str(dirty)), 1)]
        kinds = {why.split(":")[0] for _, _, why in scan(dirty)}
        expected = {"private reference material (decompiled code, evidence)", "internal note",
                    "names or describes another mod", "legacy compile guard", "personal name",
                    "developer-only source", "dated development history in a comment",
                    "development process reference in a comment", "finding or work-item id in a comment",
                    "version mismatch"}
        cases.append(("violating fixture reports every planted kind", expected <= kinds, True))
    for name, got, want in cases:
        ok = got == want
        results.append(ok)
        print(f"{'PASS' if ok else 'FAIL'}  {name} (got {got}, want {want})")
    print(f"selftest {sum(results)}/{len(results)}")
    return 0 if all(results) else 1


def main():
    if len(sys.argv) == 2 and sys.argv[1] == "--selftest":
        return selftest()
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    return check(sys.argv[1])


if __name__ == "__main__":
    sys.exit(main())
