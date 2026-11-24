import time, json, sys
sys.path.insert(0, sys.argv[1]); from u import call
t0=time.time()
while time.time()-t0 < 120:
    try:
        r=json.loads(call("clientsim_net", {"action":"status"}, timeout=10))
        d=r.get("result",{}).get("data",{})
        if d.get("ready"): print("ready after %.0fs"%(time.time()-t0)); break
    except Exception: pass
    time.sleep(2)
else: print("timeout", r)
