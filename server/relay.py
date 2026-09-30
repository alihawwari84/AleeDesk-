import socket, threading, struct, time, sys
from collections import deque

# AleeDesk relay: pairs a host and a viewer by desk ID, then forwards bytes between them.
# Session traffic is end-to-end encrypted between the two PCs; only the short handshake
# below is read here. Same wire protocol as src/Relay.cs (the relay built into the app).

waiting, misses, lock = {}, {}, threading.Lock()

def log(msg): print(time.strftime("%H:%M:%S"), msg, flush=True)

def read_exact(c, n):
    buf = b""
    while len(buf) < n:
        chunk = c.recv(n - len(buf))
        if not chunk: raise ConnectionError("closed")
        buf += chunk
    return buf

def recv_frame(c, maxlen=128):
    n = struct.unpack("<i", read_exact(c, 4))[0]
    if n <= 0 or n > maxlen: raise ValueError("bad frame")
    blob = read_exact(c, n)
    return blob[0], blob[1:]

def send_frame(c, t, data=b""):
    blob = bytes([t]) + data
    c.sendall(struct.pack("<i", len(blob)) + blob)

def valid_id(i): return 4 <= len(i) <= 16 and i.isdigit()

def is_dead(c):
    try:
        c.setblocking(False)
        try:
            return c.recv(1, socket.MSG_PEEK) == b""
        except BlockingIOError:
            return False
        except OSError:
            return True
        finally:
            c.setblocking(True)
    except Exception:
        return True

def too_many_misses(ip):
    with lock:
        q = misses.get(ip)
        if not q: return False
        now = time.time()
        while q and now - q[0] > 60: q.popleft()
        return len(q) >= 20

def record_miss(ip):
    with lock:
        misses.setdefault(ip, deque()).append(time.time())

def run_host(conn, id_):
    token = object()
    with lock:
        old = waiting.get(id_)
        if old is not None and not is_dead(old[0]):
            send_frame(conn, ord('E'), b"ID already in use")
            conn.close(); return
        if old is not None:
            try: old[0].close()
            except Exception: pass
        waiting[id_] = [conn, token]
    send_frame(conn, ord('O'))
    log(f"host {id_} registered")
    while True:
        for _ in range(10):
            time.sleep(1)
            with lock:
                cur = waiting.get(id_)
                if cur is None or cur[1] is not token: return
        with lock:
            cur = waiting.get(id_)
            if cur is None or cur[1] is not token: return
            try:
                send_frame(conn, ord('K'))
            except Exception:
                del waiting[id_]
                try: conn.close()
                except Exception: pass
                return

def pump(a, b):
    try:
        while True:
            data = a.recv(65536)
            if not data: break
            b.sendall(data)
    except Exception:
        pass
    for s in (a, b):
        try: s.shutdown(socket.SHUT_RDWR)
        except Exception: pass
        try: s.close()
        except Exception: pass

def pair(vconn, id_, ip):
    if too_many_misses(ip):
        try: send_frame(vconn, ord('E'), b"Too many attempts, try again later")
        except Exception: pass
        vconn.close(); return
    host_conn = None
    with lock:
        cur = waiting.pop(id_, None)
        if cur is not None:
            hconn, _ = cur
            if not is_dead(hconn):
                try:
                    send_frame(hconn, ord('P'), ip.encode())
                    host_conn = hconn
                except Exception:
                    try: hconn.close()
                    except Exception: pass
            else:
                try: hconn.close()
                except Exception: pass
    if host_conn is None:
        record_miss(ip)
        try:
            send_frame(vconn, ord('E'), b"Remote ID not found or offline. Check the ID and that both computers use the same relay address.")
        except Exception: pass
        vconn.close(); return
    send_frame(vconn, ord('O'))
    log(f"{ip} -> host {id_} paired")
    threading.Thread(target=pump, args=(vconn, host_conn), daemon=True).start()
    pump(host_conn, vconn)

def handle(conn, addr):
    try:
        conn.settimeout(10)
        role, data = recv_frame(conn)
        id_ = data.decode("ascii", errors="replace")
        if not valid_id(id_):
            send_frame(conn, ord('E'), b"Bad ID")
            conn.close(); return
        conn.settimeout(None)
        if role == ord('H'): run_host(conn, id_)
        elif role == ord('V'): pair(conn, id_, addr[0])
        else: conn.close()
    except Exception:
        try: conn.close()
        except Exception: pass

def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 7788
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", port))
    srv.listen(64)
    log(f"RemoteDesk relay listening on port {port} (Ctrl+C to stop)")
    while True:
        conn, addr = srv.accept()
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        conn.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
        threading.Thread(target=handle, args=(conn, addr), daemon=True).start()

if __name__ == "__main__":
    main()
