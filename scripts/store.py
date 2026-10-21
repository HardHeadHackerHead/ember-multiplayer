"""Builds the mod-site packages (Ember Multiplayer, and the mods in emberville-mods) and publishes them on Hexium
(https://emberville.hexium.gg, where Gale installs from).

    python scripts/store.py                 build, package and check Ember Multiplayer (nothing is sent)
    python scripts/store.py --no-build      package the DLL that is already built
    python scripts/store.py --publish       ...and upload it to Hexium (needs HEXIUM_AUTH_TOKEN). Ask the user first: a version can never
                                            be changed or deleted, only followed by a higher one
    python scripts/store.py --metadata      only set the categories and install location of what is on Hexium
    python scripts/store.py --mod PartyTricks [--mods-repo ..\\emberville-mods] [--publish]
                                            the same for a mod in the emberville-mods repo (next to this one by default)
    python scripts/store.py --update-readme [--mod PartyTricks]
                                            write the README's "Recent changes" (the changelog's newest 3) and stop

The package is a Thunderstore-format zip (manifest.json, icon.png, README.md, CHANGELOG.md, LICENSE.md, plugins/<Mod>/...), written to
build/store/<team>-<name>-<version>.zip.
- Ember Multiplayer: everything about it lives in store/: store.json (name, description, categories, files), icon.png
  (256 x 256) and CHANGELOG.md (a "## <version>" heading per release); the license is the repo's LICENSE.md; the version is
  Plugin.Version.
- A mod in emberville-mods: mods/<Mod>/store.json (the same fields, plus plugin_folder and icon; "{multiplayer_version}" in its
  dependencies becomes this repo's Plugin.Version), mods/<Mod>/CHANGELOG.md, mods/<Mod>/LICENSE.md, mods/<Mod>/README.md; the
  version is its [BepInPlugin] version.
The README's relative links are made absolute (raw.githubusercontent.com for images): those only show if its repo is public.
Its <!-- RECENT-CHANGES --> block must match the changelog's newest 3 versions (--update-readme writes it; a stale one stops packaging).
<!-- GITHUB-ONLY -->...<!-- END GITHUB-ONLY --> is left out on the store, <!-- STORE-ONLY: ... --> (or THUNDERSTORE-ONLY) shown only there.
Every package carries its license: the licenses say they travel with every copy.

Thunderstore has no Emberville community yet (checked 2026-10-20), so this only publishes on Hexium. The same zip works on Thunderstore
once one exists (tcli publish, community "emberville").

HEXIUM_AUTH_TOKEN is the Quads_Lab team's Hexium API token, kept in a user environment variable, never in a file.
"""
import argparse
import json
import os
import re
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from urllib.parse import urlparse

SITE = "https://hexium.gg"
ROOT = Path(__file__).resolve().parent.parent
STORE = ROOT / "store"
REPO = "HardHeadHackerHead/ember-multiplayer"
MODS_REPO = "HardHeadHackerHead/emberville-mods"


def call(method, url, token=None, body=None, raw=None):
    """One request. Returns (status, parsed JSON or text, response headers)."""
    h = {}
    data = raw
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        h["Content-Type"] = "application/json"
    if token and urlparse(url).netloc == urlparse(SITE).netloc:  # the token only ever goes to Hexium itself
        h["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            status, text, rh = r.status, r.read().decode("utf-8", "replace"), r.headers
    except urllib.error.HTTPError as e:
        status, text, rh = e.code, e.read().decode("utf-8", "replace"), e.headers
    except urllib.error.URLError as e:
        return 0, str(e.reason), {}
    try:
        return status, json.loads(text), rh
    except ValueError:
        return status, text, rh


def fail(msg):
    print("store: " + msg, file=sys.stderr)
    sys.exit(1)


def git(root, *args):
    r = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True)
    return r.stdout.strip() if r.returncode == 0 else ""


def multiplayer_version():
    src = (ROOT / "src/EmbervilleMP/Plugin.cs").read_text(encoding="utf-8")
    return re.search(r'Version = "([^"]+)"', src).group(1)


def finish(cfg):
    cfg["full"] = f"{cfg['team']}-{cfg['name']}-{cfg['version']}"
    cfg["zip"] = ROOT / "build" / "store" / f"{cfg['full']}.zip"
    return cfg


def read_config():
    """Ember Multiplayer itself."""
    cfg = json.loads((STORE / "store.json").read_text(encoding="utf-8"))
    cfg.update(version=multiplayer_version(), root=ROOT, repo=REPO, readme=ROOT / "README.md", icon=STORE / "icon.png",
               changelog=STORE / "CHANGELOG.md", license=ROOT / "LICENSE.md", plugin_folder="EmbervilleMP",
               sources=[ROOT / "src"], tracked=["src", "store", "README.md", "LICENSE.md"])
    return finish(cfg)


def read_mod_config(mods_root, mod):
    """A mod in the emberville-mods repo (mods/<mod>/store.json)."""
    folder = mods_root / "mods" / mod
    if not (folder / "store.json").exists():
        fail(f"no {folder / 'store.json'}")
    cfg = json.loads((folder / "store.json").read_text(encoding="utf-8"))
    src = "\n".join(p.read_text(encoding="utf-8") for p in folder.glob("*.cs"))
    m = re.search(r'const string Version = "([^"]+)"', src) or re.search(r'BepInPlugin\([^)]*"(\d+\.\d+\.\d+)"\)', src)
    if not m:
        fail(f"no version in {folder}'s sources ([BepInPlugin(..., \"1.0.0\")] or a Version constant)")
    mp = multiplayer_version()
    cfg["dependencies"] = [d.replace("{multiplayer_version}", mp) for d in cfg["dependencies"]]
    cfg.update(version=m.group(1), root=mods_root, repo=MODS_REPO, readme=folder / "README.md",
               icon=mods_root / cfg.get("icon", f"mods/{mod}/icon.png"), changelog=folder / "CHANGELOG.md",
               license=folder / "LICENSE.md", plugin_folder=cfg.get("plugin_folder", mod), sources=[folder],
               tracked=[f"mods/{mod}"])
    return finish(cfg)


def vkey(v):
    return tuple(int(x) for x in v.split("."))


def png_size(path):
    with open(path, "rb") as f:
        head = f.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    return struct.unpack(">II", head[16:24])


RECENT = re.compile(r"(<!-- RECENT-CHANGES[^>]*-->\n)(.*?)(<!-- END RECENT-CHANGES -->)", re.S)


def recent_changes(cfg, count=3):
    """The changelog's newest versions as one line each: '- **1.2.0** what changed'."""
    text = cfg["changelog"].read_text(encoding="utf-8") if cfg["changelog"].exists() else ""
    lines = []
    for m in re.finditer(r"^## (\S+)\s*\n(.*?)(?=^## |\Z)", text, flags=re.S | re.M):
        items = [re.sub(r"\s+", " ", i).strip() for i in re.split(r"^- ", m.group(2), flags=re.M) if i.strip()]
        lines.append(f"- **{m.group(1)}** " + " ".join(items))
        if len(lines) == count:
            break
    return "\n".join(lines) + "\n"


def update_readme(cfg):
    text = cfg["readme"].read_text(encoding="utf-8")
    if not RECENT.search(text):
        fail(f"{cfg['readme'].relative_to(cfg['root'])} has no <!-- RECENT-CHANGES --> block")
    new = RECENT.sub(lambda m: m.group(1) + recent_changes(cfg) + m.group(3), text)
    if new != text:
        cfg["readme"].write_text(new, encoding="utf-8")
    print(f"  {cfg['readme'].relative_to(cfg['root'])}: recent changes {'written' if new != text else 'already current'}")


def store_readme(cfg, branch):
    """The README as the mod site shows it: <!-- GITHUB-ONLY -->...<!-- END GITHUB-ONLY --> left out, relative links absolute."""
    text = cfg["readme"].read_text(encoding="utf-8")
    text = re.sub(r"<!-- GITHUB-ONLY -->.*?<!-- END GITHUB-ONLY -->\s*", "", text, flags=re.S)
    text = re.sub(r"<!-- (?:STORE|THUNDERSTORE)-ONLY:(.*?)-->", r"\1", text, flags=re.S)
    here = cfg["readme"].parent.relative_to(cfg["root"]).as_posix()   # links are relative to the README's own folder
    here = "" if here == "." else here + "/"
    raw = f"https://raw.githubusercontent.com/{cfg['repo']}/{branch}/{here}"
    blob = f"https://github.com/{cfg['repo']}/blob/{branch}/{here}"
    absolute = lambda base: (lambda m: m.group(1) + (base + m.group(2) if not re.match(r"[a-z]+:|#", m.group(2)) else m.group(2)) + m.group(3))
    text = re.sub(r'(<img[^>]*?src=")([^"]+)(")', absolute(raw), text)
    text = re.sub(r"(!\[[^\]]*\]\()([^)\s]+)(\))", absolute(raw), text)
    text = re.sub(r"((?<!!)\[[^\]]*\]\()([^)\s]+)(\))", absolute(blob), text)
    return text


def check(cfg, built_now):
    """Everything an upload needs. Returns (problems, warnings)."""
    problems, warnings = [], []
    root = cfg["root"]
    if not re.fullmatch(r"[A-Za-z0-9_]+", cfg["name"]):
        problems.append(f"name {cfg['name']!r}: letters, digits and _ only")
    if not re.fullmatch(r"\d+\.\d+\.\d+", cfg["version"]):
        problems.append(f"version {cfg['version']} is not Major.Minor.Patch")
    if len(cfg["description"]) > 250 or "TODO" in cfg["description"]:
        problems.append(f"description: 250 characters at most, no TODO (it is {len(cfg['description'])})")
    icon = cfg["icon"]
    if not icon.exists():
        problems.append(f"no {icon.relative_to(root)} (a 256 x 256 PNG)")
    elif png_size(icon) != (256, 256):
        problems.append(f"{icon.relative_to(root)} is {png_size(icon)}, not a 256 x 256 PNG")
    changelog = cfg["changelog"].read_text(encoding="utf-8") if cfg["changelog"].exists() else ""
    if f"## {cfg['version']}" not in changelog:
        problems.append(f"{cfg['changelog'].relative_to(root)} has no '## {cfg['version']}' heading")
    m = RECENT.search(cfg["readme"].read_text(encoding="utf-8"))
    if m and m.group(2) != recent_changes(cfg):
        problems.append(f"{cfg['readme'].relative_to(root)}'s recent changes don't match the changelog: run with --update-readme")
    if not cfg["license"].exists():
        problems.append(f"no {cfg['license'].relative_to(root)}: every package carries its license")
    for f in cfg["files"]:
        if not (root / f).exists():
            problems.append(f"missing {f} (build first)")
    dll = root / cfg["files"][0]
    if dll.exists() and not built_now:
        newest = max((p.stat().st_mtime, p) for d in cfg["sources"] for p in d.rglob("*.cs") if "obj" not in p.parts)
        if newest[0] > dll.stat().st_mtime:
            problems.append(f"{newest[1].relative_to(root)} is newer than the DLL: build again (drop --no-build)")
    if dll.exists() and b"AI_Assisted_Creation" not in dll.read_bytes():
        warnings.append("the DLL has no AI_Assisted_Creation AssemblyMetadata (Thunderstore requires it; see the publish skill)")
    if git(root, "status", "--porcelain", "--", *cfg["tracked"]):
        warnings.append(f"uncommitted changes in {', '.join(cfg['tracked'])}: the package may hold work that isn't in git")
    # the README's pictures must load for anyone (a private repo's won't)
    readme = store_readme(cfg, git(root, "rev-parse", "--abbrev-ref", "HEAD") or "main")
    if not re.search(r"Claude|AI", readme):
        warnings.append("the README doesn't say AI helped make the mod (add 'Made with the help of Claude (Anthropic).')")
    broken = []
    for url in sorted(set(re.findall(r'https://raw\.githubusercontent\.com/[^\s")]+', readme))):
        s, _, _ = call("HEAD", url)
        if s != 200:
            broken.append(url.rsplit("/", 1)[-1])
    if broken:
        problems.append(f"{len(broken)} README image(s) don't load publicly ({', '.join(broken[:4])}...): make the repo public, "
                        "or push them first")
    # the version must be new on Hexium and higher than what is there
    s, r, _ = call("GET", f"{SITE}/api/experimental/package/{cfg['team']}/{cfg['name']}/")
    if s == 200:
        latest = r["latest"]["version_number"]
        if vkey(cfg["version"]) <= vkey(latest):
            problems.append(f"Hexium already has {latest}: raise the version above it")
    return problems, warnings, readme


def package(cfg, readme):
    manifest = {"name": cfg["name"], "version_number": cfg["version"], "website_url": cfg["website"],
                "description": cfg["description"], "dependencies": cfg["dependencies"]}
    folder = "plugins/" + cfg["plugin_folder"] + "/"
    cfg["zip"].parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(cfg["zip"], "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest, indent=2, ensure_ascii=False))
        z.writestr("README.md", readme)
        z.write(cfg["icon"], "icon.png")
        z.write(cfg["changelog"], "CHANGELOG.md")
        z.write(cfg["license"], "LICENSE.md")
        z.write(cfg["license"], folder + "LICENSE.md")   # (also next to the DLL: mod managers install only plugins/)
        for f in cfg["files"]:  # steam_api64.dll beside EmbervilleMP.dll: SteamBootstrap loads it from there
            z.write(cfg["root"] / f, folder + Path(f).name)


def native_ack(cfg):
    """Hexium holds packages with native DLLs (steam_api64.dll: Valve's, no PDB) for a moderator unless the upload acknowledges they
    have no PDB. The submit field isn't documented: these are the names tried (unknown fields are ignored)."""
    native = [f"plugins/{cfg['plugin_folder']}/" + Path(f).name for f in cfg["files"] if Path(f).name.lower() == "steam_api64.dll"]
    if not native:
        return {}
    return {"acknowledge_missing_pdbs": True, "acknowledge_missing_pdb": True, "missing_pdb_acknowledged": True,
            "pdb_acknowledgement": True, "pdb_acknowledged": True, "acknowledged_missing_pdbs": native,
            "acknowledged_native_binaries": native, "native_binaries_acknowledged": True}


def upload(cfg, token):
    data = cfg["zip"].read_bytes()
    community = cfg["hexium"]["community"]
    for attempt in range(15):
        s, r, _ = call("POST", f"{SITE}/api/experimental/usermedia/initiate-upload/", token,
                       {"filename": cfg["zip"].name, "file_size_bytes": len(data)})
        if s not in (200, 201):
            fail(f"initiate-upload: {s} {r}")
        uuid, parts = r["user_media"]["uuid"], []
        for part in r["upload_urls"]:
            chunk = data[part["offset"]:part["offset"] + part["length"]]
            s, body, rh = call("PUT", part["url"], token, raw=chunk)
            if s not in (200, 201, 204) or not rh.get("ETag"):
                call("POST", f"{SITE}/api/experimental/usermedia/{uuid}/abort-upload/", token, {})
                fail(f"upload part {part['part_number']}: {s} {body}")
            parts.append({"ETag": rh["ETag"], "PartNumber": part["part_number"]})
        s, r, _ = call("POST", f"{SITE}/api/experimental/usermedia/{uuid}/finish-upload/", token, {"parts": parts})
        if s not in (200, 201):
            fail(f"finish-upload: {s} {r}")
        s, r, _ = call("POST", f"{SITE}/api/experimental/submission/submit/", token, {
            "upload_uuid": uuid, "author_name": cfg["team"], "communities": [community], "has_nsfw_content": False,
            "categories": [], "community_categories": {community: cfg["hexium"]["categories"]},
            **native_ack(cfg)})
        if s in (200, 201):
            return r
        if "too many" not in json.dumps(r):  # Hexium's limit: 10 uploads per 10 minutes per team (a refused upload is used up)
            break
        print(f"   Hexium's upload limit: trying again in a minute ({time.strftime('%H:%M')})", flush=True)
        time.sleep(60)
    fail(f"submit: {s} {r}")


def set_metadata(cfg, token):
    s, r, _ = call("POST", f"{SITE}/api/mod/{cfg['team']}/{cfg['name']}/set-metadata", token,
                   {"install_location": cfg["hexium"]["install_location"], "categories": cfg["hexium"]["categories"]})
    if s != 200:
        fail(f"set-metadata: {s} {r}")
    return r


def main():
    ap = argparse.ArgumentParser(description="Package Ember Multiplayer (or one of the emberville-mods) for the mod sites and publish it on Hexium")
    ap.add_argument("--publish", action="store_true", help="upload to Hexium (otherwise only builds and checks)")
    ap.add_argument("--no-build", action="store_true", help="use the DLL that is already built")
    ap.add_argument("--metadata", action="store_true", help="only set categories and install location on Hexium")
    ap.add_argument("--mod", help="a mod in the emberville-mods repo (its folder name under mods/, e.g. PartyTricks)")
    ap.add_argument("--mods-repo", default=str(ROOT.parent / "emberville-mods"), help="the emberville-mods checkout")
    ap.add_argument("--update-readme", action="store_true", help="write the README's recent changes from the changelog, then stop")
    a = ap.parse_args()
    # (a token saved with setx only reaches terminals opened after it: fall back to the user's registry value)
    token = os.environ.get("HEXIUM_AUTH_TOKEN")
    if not token and os.name == "nt":
        r = subprocess.run(["powershell", "-NoProfile", "-Command",
                            "[Environment]::GetEnvironmentVariable('HEXIUM_AUTH_TOKEN','User')"], capture_output=True, text=True)
        token = r.stdout.strip() or None
    if (a.publish or a.metadata) and not token:
        fail("set HEXIUM_AUTH_TOKEN to the Quads_Lab team's Hexium API token first (in your own terminal, never in a file)")
    mods_root = Path(a.mods_repo).resolve()
    cfg = read_mod_config(mods_root, a.mod) if a.mod else read_config()
    if a.update_readme:
        update_readme(cfg)
        return
    if a.metadata:
        print("metadata set:", set_metadata(cfg, token))
        return
    if not a.no_build:
        # this repo's build also compiles the mods in emberville-mods against it (EMB_MODS says where)
        env = dict(os.environ, EMB_MODS=str(mods_root))
        r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "scripts/build.ps1"), "-NoDeploy"], env=env)
        if r.returncode != 0:
            fail("build failed")
    print(f"== {cfg['full']}  ({cfg['hexium']['community']} on Hexium: {', '.join(cfg['hexium']['categories'])}, "
          f"install: {cfg['hexium']['install_location']})")
    problems, warnings, readme = check(cfg, built_now=not a.no_build)
    for w in warnings:
        print("  warning: " + w)
    for p in problems:
        print("  PROBLEM: " + p)
    if problems:
        fail(f"{len(problems)} problem(s): nothing packaged")
    package(cfg, readme)
    print(f"  ready: {cfg['zip'].relative_to(ROOT)} ({cfg['zip'].stat().st_size // 1024} KB)")
    if not a.publish:
        print("  test it: Gale or r2modman > a new, empty Emberville profile > import local mod > that zip, then play a session")
        return
    r = upload(cfg, token)
    print(f"  uploaded{' (hidden while it is virus-scanned)' if r.get('hidden') else ''}: "
          f"https://{cfg['hexium']['community']}.hexium.gg/mods/{cfg['team']}/{cfg['name']}")
    print("  metadata set:", set_metadata(cfg, token))


if __name__ == "__main__":
    main()
