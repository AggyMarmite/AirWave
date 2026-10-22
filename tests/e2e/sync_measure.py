import os, sys, time, select, struct
# Records beep onsets (host monotonic time) from each receiver's output pipe and compares them.
paths = {"rx1": sys.argv[2], "rx2": sys.argv[3]}
dur = float(sys.argv[1]) if len(sys.argv) > 1 else 20
fds = {n: os.open(p, os.O_RDONLY | os.O_NONBLOCK) for n, p in paths.items()}
st = {n: {"buf": b"", "frames": 0, "last": -10**9, "on": []} for n in paths}
T0 = time.monotonic()
while time.monotonic() - T0 < dur:
    r, _, _ = select.select(list(fds.values()), [], [], 0.2)
    now = time.monotonic()
    for n, fd in fds.items():
        if fd not in r:
            continue
        try:
            b = os.read(fd, 65536)
        except BlockingIOError:
            continue
        if not b:
            time.sleep(0.01)
            continue
        s = st[n]
        s["buf"] += b
        k = len(s["buf"]) // 8
        chunk = s["buf"][:k * 8]
        s["buf"] = s["buf"][k * 8:]
        v = struct.unpack('<%di' % (k * 2), chunk)
        for i in range(k):
            if (abs(v[2 * i]) >> 16) > 300 and s["frames"] + i - s["last"] > 24000:
                s["on"].append(now - (k - i) / 48000.0)
                s["last"] = s["frames"] + i
        s["frames"] += k
a = st["rx1"]["on"]
b = st["rx2"]["on"]
print("rx1 beeps", len(a), "rx2 beeps", len(b))
d = []
for x in b:
    if not a:
        break
    y = min(a, key=lambda z: abs(z - x))
    if abs(y - x) < 0.5:
        d.append(round((x - y) * 1000, 1))
print("rx2-rx1 diffs ms:", d, flush=True)
