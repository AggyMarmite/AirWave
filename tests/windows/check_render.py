import sys, re
# Compares beep onsets recorded from Windows' output (receive --render) with the sender's anchor.
rx = open(sys.argv[1], encoding="utf-8", errors="replace").read()
tx = open(sys.argv[2], encoding="utf-8", errors="replace").read()
anchor = float(re.search(r"ANCHOR monotonic ([\d.]+)", tx).group(1))
onsets = [float(x) for x in re.findall(r"ONSET ([\d.]+)", rx)]
enc = 1024 / 44100
errs = []
for o in onsets:
    if o < anchor - 0.5:
        continue
    k = round(o - anchor - enc)
    errs.append(round((o - (anchor + k + enc)) * 1000, 1))
print("Windows playback timing error vs schedule (ms):", errs)
assert len(errs) >= 6, "not enough beeps came out of the Windows audio device"
worst = max(abs(e) for e in errs)
spread = max(errs) - min(errs)
assert worst <= 20 and spread <= 5, f"Windows playback off schedule (worst {worst} ms, spread {spread} ms)"
print(f"PASS: AirPlay audio plays through Windows on schedule (worst {worst} ms, spread {spread} ms)")
