# stop play -> refresh/compile -> wait -> errors -> play -> ready
import sys, json, time; sys.path.insert(0, sys.argv[1]); from u import call
def safe(t, p, to=30):
    try: return json.loads(call(t, p, timeout=to))
    except Exception as e: return {"exc": str(e)}
safe("manage_editor", {"action":"stop"}); time.sleep(2)
safe("refresh_unity", {"mode":"force","scope":"all","compile":"request","wait_for_ready":False}); time.sleep(5)
t0=time.time()
while time.time()-t0 < 240:
    r = safe("clientsim_net", {"action":"status"}, 10)
    if r.get("status")=="success": break
    time.sleep(3)
time.sleep(2)
e = safe("read_console", {"action":"get","types":["error"],"count":10})
print("errors:", json.dumps(e.get("result",{}).get("data"), ensure_ascii=False)[:2000])
if len(sys.argv) > 2 and sys.argv[2] == "play":
    safe("read_console", {"action":"clear"})
    safe("manage_editor", {"action":"play"})
    t0=time.time()
    while time.time()-t0 < 120:
        r = safe("clientsim_net", {"action":"status"}, 10)
        if r.get("result",{}).get("data",{}).get("ready"): print("ready"); break
        time.sleep(2)
