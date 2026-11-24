import sys, json, time; sys.path.insert(0, sys.argv[1]); from u import call
def safe(t, p, to=30):
    try: return json.loads(call(t, p, timeout=to))
    except Exception as e: return {"exc": str(e)}
safe("manage_editor", {"action":"stop"}); time.sleep(3)
safe("read_console", {"action":"clear"})
safe("manage_editor", {"action":"play"})
t0=time.time()
while time.time()-t0 < 120:
    r = safe("clientsim_net", {"action":"status"}, 10)
    if r.get("result",{}).get("data",{}).get("ready"): print("ready"); break
    time.sleep(2)
