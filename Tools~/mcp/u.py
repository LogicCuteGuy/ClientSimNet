import socket, struct, json, sys
import sys; sys.stdout.reconfigure(encoding="utf-8")
def call(t, params=None, timeout=60):
    s = socket.create_connection(("127.0.0.1", 6400), timeout=timeout)
    s.recv(64)  # WELCOME handshake
    body = json.dumps({"type": t, "params": params or {}}).encode()
    s.sendall(struct.pack(">Q", len(body)) + body)
    def rd(n):
        b = b""
        while len(b) < n:
            c = s.recv(n - len(b))
            if not c: raise EOFError
            b += c
        return b
    n = struct.unpack(">Q", rd(8))[0]
    r = rd(n).decode("utf-8", "replace"); s.close(); return r
if __name__ == "__main__":
    print(call(sys.argv[1], json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}))
