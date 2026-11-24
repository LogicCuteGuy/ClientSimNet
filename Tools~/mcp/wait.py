import time, json, sys
sys.path.insert(0, sys.argv[1]); from u import call
t0=time.time()
while time.time()-t0 < 240:
    try:
        r=json.loads(call("clientsim_net", {"action":"status"}, timeout=10))
        if r.get("status")=="success": print("up after %.0fs"%(time.time()-t0), r["result"]["data"]); break
    except Exception as e: pass
    time.sleep(3)
