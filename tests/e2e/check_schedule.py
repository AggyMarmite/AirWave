import sys, re
# Checks AirWave's beep onsets against the exact instants the sender's anchor(s) say they should be heard,
# and (with a pause) that nothing plays while paused.
t = open(sys.argv[1]).read()
p = open(sys.argv[2]).read()
anchor = float(re.search(r"ANCHOR monotonic ([\d.]+)", p).group(1))
m2 = re.search(r"ANCHOR2 monotonic ([\d.]+) offset (\d+)", p)
mp = re.search(r"PAUSED monotonic ([\d.]+)", p)
on1 = [float(x) for x in re.search(r"rx1 onsets: (.*)", t).group(1).split()]
on2 = [float(x) for x in re.search(r"rx2 onsets: (.*)", t).group(1).split()]
enc = 1024 / 44100  # AAC encoder start-up delay: each beep comes out 1024 samples late

def err(o, base, shift):
    k = round(o - base - shift)
    return round((o - (base + k + shift)) * 1000, 1)

mr = re.search(r"RESTREAM monotonic ([\d.]+)", p)
errs, during_pause = [], []
for o in on2:
    if mr and 0 <= o - float(mr.group(1)) < 0.15:
        continue  # a beep cut short by the stream restart, picked up again as the new stream starts
    if m2 and abs(o - float(m2.group(1))) < 0.12:
        continue  # the rest of a beep that was cut off by the pause, heard as soon as playback resumes
    if m2 and o > float(m2.group(1)) - 0.05:
        errs.append(err(o, float(m2.group(1)), enc - int(m2.group(2)) / 44100))
    elif mp and o > float(mp.group(1)) + 0.15:
        during_pause.append(round(o, 3))
    else:
        errs.append(err(o, anchor, enc))
print("AirWave timing error vs schedule (ms):", errs)
assert len(on1) >= 6, "Shairport Sync didn't receive the stream"
assert len(errs) >= 8, "AirWave didn't play enough audio"
assert not during_pause, f"AirWave kept playing while paused: {during_pause}"
if m2:
    after = [o for o in on2 if o > float(m2.group(1))]
    assert len(after) >= 3, "AirWave didn't resume after the pause"
worst = max(abs(e) for e in errs)
assert worst <= 5, f"AirWave off schedule by {worst} ms"
print(f"PASS: buffered AAC plays on schedule{' through a pause' if m2 else ''} (worst {worst} ms over {len(errs)} beeps)")
