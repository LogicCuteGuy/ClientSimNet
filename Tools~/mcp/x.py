import sys, json; sys.path.insert(0, sys.argv[1]); from u import call
code = open(sys.argv[2], encoding="utf-8").read()
r = json.loads(call("execute_code", {"action":"execute","code":code,"safety_checks":False}, timeout=120))
res = r.get("result", r)
print(json.dumps(res.get("data", res), ensure_ascii=False, indent=1)[:6000] if isinstance(res, dict) else res)
