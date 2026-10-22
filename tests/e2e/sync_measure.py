import os, sys, time, select, struct
# Records beep onsets (host monotonic time) from each receiver's output pipe and compares them.
# Each pipe is "path" (Shairport Sync's s32le 48 kHz) or "path:s16:44100"
def spec(a):
    parts = a.split(":")
    return parts[0], (parts[1] if len(parts) > 1 else "s32"), (int(parts[2]) if len(parts) > 2 else 48000)
specs = {"rx1": spec(sys.argv[2]), "rx2": spec(sys.argv[3])}
paths = {n: s[0] for n, s in specs.items()}
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
        fmt, rate = specs[n][1], specs[n][2]
        bpf = 8 if fmt == "s32" else 4
        k = len(s["buf"]) // bpf
        chunk = s["buf"][:k * bpf]
        s["buf"] = s["buf"][k * bpf:]
        v = struct.unpack(('<%di' if fmt == "s32" else '<%dh') % (k * 2), chunk)
        shift = 16 if fmt == "s32" else 0
        for i in range(k):
            if (abs(v[2 * i]) >> shift) > 300 and s["frames"] + i - s["last"] > rate // 2:
                s["on"].append(now - (k - i) / float(rate))
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
print("rx1 onsets:", " ".join("%.4f" % x for x in a))
print("rx2 onsets:", " ".join("%.4f" % x for x in b), flush=True)
