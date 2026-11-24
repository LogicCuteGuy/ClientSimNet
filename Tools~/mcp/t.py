import sys, json, time; sys.path.insert(0, sys.argv[1]); from u import call
sys.stdout.reconfigure(encoding="utf-8")
steps = json.load(open(sys.argv[2], encoding="utf-8"))
seen = None
def net(p):
    r = json.loads(call("clientsim_net", p, timeout=60)); return r.get("result", r)
for st in steps:
    if "sleep" in st: time.sleep(st["sleep"]); continue
    show = st.pop("show", None)
    st.setdefault("log", 300)
    res = net(st)
    d = res.get("data") or {}
    print(f"## {st['action']} {st.get('player','')} {st.get('obj','')} -> {'OK' if res.get('success') else 'ERR ' + str(res.get('error'))}")
    log = d.get("log", []) if isinstance(d, dict) else []
    key = lambda l: (l["t"], l["text"])
    if seen is None: seen = set()
    for l in log:
        if key(l) in seen: continue
        seen.add(key(l))
        if "Spinner" in l["text"] or "Pose" in l["text"]: continue
        print(f"   {l['t']:7.2f} [{l['player']}] {l['kind']:4} {l['type'][:4]} {l['text'][:170]}")
    if show and isinstance(d, dict):
        for o in d.get("objects", []):
            if any(s in o["path"] for s in show): print(f"   = {o['path'].split('/')[-1][:28]:28} owner={o['owner']} inSync={o['inSync']} local={o['local'][:90]}")
        if "players" in show:
            for p in d.get("players", []): print(f"   = player {p['id']} {p['name']} local={p['isLocal']} master={p['isMaster']} mode={p['mode']} pos={p['position']}")
