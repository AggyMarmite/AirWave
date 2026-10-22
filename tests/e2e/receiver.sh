#!/usr/bin/env bash
# End-to-end test of AirWave as an AirPlay receiver.
# rx1: Shairport Sync (the reference AirPlay 2 receiver), rx2: AirWave's receiver, each in its own network namespace.
# A sender plays a beeping tone to both at once; the test checks AirWave plays it intact and in sync with Shairport Sync.
#   sudo tests/e2e/receiver.sh <work-dir> [realtime|buffered]   (work-dir must already contain built nqptp + shairport-sync, see run.sh)
set -euo pipefail
WORK=${1:-/tmp/airwave-e2e}
MODE=${2:-realtime}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
cd "$WORK"
SPS=$WORK/shairport-sync/shairport-sync
NQPTP=$WORK/nqptp/nqptp

(cd "$REPO/src/AirWave.TestCli" && dotnet build -c Release -v q -nologo > "$WORK/build.log" 2>&1) || { cat "$WORK/build.log"; exit 1; }
CLI="dotnet $REPO/src/AirWave.TestCli/bin/Release/net8.0/AirWave.TestCli.dll"

for i in 1 2; do
  ns=awrx$i
  ip netns del $ns 2>/dev/null || true
  ip link del awv$i 2>/dev/null || true
  ip netns add $ns
  ip link add awv$i type veth peer name awp$i
  ip link set awp$i netns $ns
  ip addr add 10.9$i.0.1/24 dev awv$i
  ip link set awv$i up
  ip netns exec $ns ip addr add 10.9$i.0.2/24 dev awp$i
  ip netns exec $ns ip link set awp$i up
  ip netns exec $ns ip link set lo up
  ip netns exec $ns ip route add 224.0.0.0/4 dev awp$i
  rm -f rx$i.pcm && mkfifo rx$i.pcm
done
ip route replace 224.0.0.0/4 dev awv2 2>/dev/null || true

# rx1: Shairport Sync with its own dbus/avahi/nqptp
cat > rx1.conf <<CONF
general = { name = "E2E-Rx1"; output_backend = "pipe"; port = 7000; };
pipe = { name = "$WORK/rx1.pcm"; };
CONF
cat > start1.sh <<SH
mount -t tmpfs none /run/dbus 2>/dev/null || mkdir -p /run/dbus
mkdir -p /run/avahi-daemon && mount -t tmpfs none /run/avahi-daemon
dbus-daemon --system --fork
sleep 0.5
avahi-daemon --no-chroot --no-drop-root -D
sleep 1
$NQPTP > $WORK/nqptp1.log 2>&1 &
sleep 0.5
exec $SPS -c $WORK/rx1.conf -v > $WORK/sps1.log 2>&1
SH
mkdir -p /run/dbus
(ip netns exec awrx1 unshare -m sh $WORK/start1.sh &)

# rx2: AirWave receiver
(ip netns exec awrx2 $CLI receive --name "E2E-AirWave" --pipe $WORK/rx2.pcm --port 7000 --id-dir $WORK/rxid --debug > $WORK/airwave-rx.log 2>&1 &)

cleanup() {
  for i in 1 2; do for p in $(ip netns pids awrx$i 2>/dev/null); do kill $p 2>/dev/null || true; done; ip netns del awrx$i 2>/dev/null || true; done
}
trap cleanup EXIT

for i in 1 2; do
  for t in $(seq 1 60); do ip netns exec awrx$i ss -lnt | grep -q ':7000' && break; sleep 0.25; done
done
grep -q READY $WORK/airwave-rx.log || { echo "FAIL: AirWave receiver didn't start"; cat $WORK/airwave-rx.log; exit 1; }

echo "== discovery (AirWave receiver advertises itself)"
$CLI discover 5 | tee discover.txt | grep FOUND || true
grep -q "E2E-AirWave" discover.txt || { echo "FAIL: AirWave receiver not discovered"; exit 1; }

echo "== $MODE stream to Shairport Sync and AirWave together"
python3 "$HERE/sync_measure.py" 18 "$WORK/rx1.pcm" "$WORK/rx2.pcm:s16:44100" > "$WORK/rsync.txt" &
MPID=$!
sleep 0.5
if [[ "$MODE" == "buffered" ]]; then
  [[ -f beeps.adts ]] || ffmpeg -hide_banner -loglevel error -y -f lavfi -i "aevalsrc=0.25*sin(2*PI*440*t)*lt(mod(t\,1)\,0.1):s=44100:c=stereo:d=20" -c:a aac -b:a 256k -f adts beeps.adts
  $CLI send-buffered 10.91.0.2:7000,10.92.0.2:7000 --aac "$WORK/beeps.adts" --seconds 14 ${PAUSE_AT:+--pause-at $PAUSE_AT} | tee "$WORK/play.txt" | grep -E "SESSION|END|ERR" || true
else
  $CLI play 10.91.0.2:7000,10.92.0.2:7000 --seconds 14 --delay 2000 | tee "$WORK/play.txt" | grep -E "SESSION|END" || true
fi
wait $MPID
grep -v onsets "$WORK/rsync.txt"
if [[ "$MODE" == "buffered" ]]; then
  # Buffered audio is anchored to an exact instant, so check AirWave against the true schedule
  # (Shairport Sync's pipe output can't keep time precisely in this mode, so it only has to receive audio).
  python3 "$HERE/check_schedule.py" "$WORK/rsync.txt" "$WORK/play.txt"
  exit $?
fi
python3 - "$WORK/rsync.txt" <<'PY'
import sys, re
t = open(sys.argv[1]).read()
m = re.search(r"rx1 beeps (\d+) rx2 beeps (\d+)", t)
d = [float(x) for x in re.findall(r"-?\d+\.\d+", t.split("diffs ms:")[1].split("\n")[0])] if "diffs ms:" in t else []
assert m and int(m.group(1)) >= 8 and int(m.group(2)) >= 8, "not enough audio arrived"
assert len(d) >= 8, "too few matched beeps"
worst = max(abs(x) for x in d)
assert worst <= 15, f"AirWave out of sync with Shairport Sync by {worst} ms"
print(f"PASS: AirWave receiver plays in sync with Shairport Sync (worst {worst} ms over {len(d)} beeps)")
PY
