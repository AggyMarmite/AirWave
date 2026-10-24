#!/usr/bin/env bash
# End-to-end test: streams from the AirWave engine to two real AirPlay 2 receivers
# (shairport-sync in AirPlay 2 mode, each with its own PTP daemon in its own network namespace),
# the second joining a few seconds late, then checks the audio arrived intact and both play in sync.
# Needs root (network namespaces). Usage: sudo tests/e2e/run.sh <work-dir> [--skip-build]
set -euo pipefail
WORK=${1:-/tmp/airwave-e2e}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
mkdir -p "$WORK"
cd "$WORK"

if [[ "${2:-}" != "--skip-build" ]]; then
  apt-get install -y -q iproute2 build-essential autoconf automake libtool pkg-config libpopt-dev libconfig-dev \
    libssl-dev libsoxr-dev libplist-dev libsodium-dev libgcrypt20-dev libavutil-dev libavcodec-dev libavformat-dev \
    uuid-dev xxd libavahi-client-dev avahi-daemon dbus > /dev/null
  [[ -d nqptp ]] || git clone -q --depth 1 https://github.com/mikebrady/nqptp.git
  (cd nqptp && autoreconf -fi > /dev/null 2>&1 && ./configure > /dev/null && make -j"$(nproc)" > /dev/null)
  [[ -d shairport-sync ]] || git clone -q --depth 1 https://github.com/mikebrady/shairport-sync.git
  (cd shairport-sync && autoreconf -fi > /dev/null 2>&1 && ./configure --with-pipe --with-avahi --with-ssl=openssl --with-airplay-2 --with-soxr > /dev/null && make -j"$(nproc)" > /dev/null)
fi
SPS=$WORK/shairport-sync/shairport-sync
NQPTP=$WORK/nqptp/nqptp

# Two receivers, each in its own network namespace with its own dbus/avahi/nqptp
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
  rm -f rx$i.pcm && mkfifo rx$i.pcm
  cat > rx$i.conf <<CONF
general = { name = "E2E-Rx$i"; output_backend = "pipe"; port = 7000; };
pipe = { name = "$WORK/rx$i.pcm"; };
CONF
  cat > start$i.sh <<SH
mount -t tmpfs none /run/dbus 2>/dev/null || mkdir -p /run/dbus
mkdir -p /run/avahi-daemon && mount -t tmpfs none /run/avahi-daemon
dbus-daemon --system --fork
sleep 0.5
avahi-daemon --no-chroot --no-drop-root -D
sleep 1
$NQPTP > $WORK/nqptp$i.log 2>&1 &
sleep 0.5
exec $SPS -c $WORK/rx$i.conf -v > $WORK/sps$i.log 2>&1
SH
  mkdir -p /run/dbus
  (ip netns exec $ns unshare -m sh $WORK/start$i.sh &) 
done
for i in 1 2; do
  for t in $(seq 1 40); do ip netns exec awrx$i ss -lnt | grep -q ':7000' && break; sleep 0.25; done
done

cleanup() {
  for i in 1 2; do for p in $(ip netns pids awrx$i 2>/dev/null); do kill $p 2>/dev/null || true; done; ip netns del awrx$i 2>/dev/null || true; done
}
trap cleanup EXIT

cd "$REPO/src/AirWave.TestCli"
dotnet build -c Release -v q -nologo > "$WORK/build.log" 2>&1 || { cat "$WORK/build.log"; exit 1; }
CLI="dotnet bin/Release/net8.0/AirWave.TestCli.dll"

echo "== discovery"
$CLI discover 6 | tee "$WORK/discover.txt" | grep FOUND || true
grep -q "E2E-Rx1" "$WORK/discover.txt" && grep -q "E2E-Rx2" "$WORK/discover.txt" || { echo "FAIL: discovery didn't find both receivers"; exit 1; }

echo "== streaming to two receivers (second joins 3 s late)"
python3 "$HERE/sync_measure.py" 24 "$WORK/rx1.pcm" "$WORK/rx2.pcm" > "$WORK/sync.txt" &
MPID=$!
sleep 0.5
$CLI play 10.91.0.2:7000,10.92.0.2:7000 --seconds 12 --delay 2000 --stagger 3 | tee "$WORK/play.txt" | grep -E "SESSION|END"
wait $MPID
cat "$WORK/sync.txt"
python3 - "$WORK/sync.txt" <<'PY'
import sys, re
t = open(sys.argv[1]).read()
m = re.search(r"rx1 beeps (\d+) rx2 beeps (\d+)", t)
d = [float(x) for x in re.findall(r"-?\d+\.\d+", t.split("diffs ms:")[1].split("\n")[0])] if "diffs ms:" in t else []
assert m and int(m.group(1)) >= 9 and int(m.group(2)) >= 6, "not enough audio arrived"
assert len(d) >= 6, "too few matched beeps"
worst = max(abs(x) for x in d)
assert worst <= 15, f"speakers out of sync by {worst} ms"
print(f"PASS: audio intact, both receivers in sync (worst {worst} ms over {len(d)} beeps)")
PY

echo "== timing nudge: second receiver moved 120 ms later while playing"
python3 "$HERE/sync_measure.py" 22 "$WORK/rx1.pcm" "$WORK/rx2.pcm" > "$WORK/offset.txt" &
MPID=$!
sleep 0.5
$CLI play 10.91.0.2:7000,10.92.0.2:7000 --seconds 18 --delay 2000 --offset2 120 --offset-after 8 | tee "$WORK/play-offset.txt" | grep -E "SESSION|OFFSET|END"
wait $MPID
cat "$WORK/offset.txt"
python3 - "$WORK/offset.txt" <<'PY2'
import sys, re
t = open(sys.argv[1]).read()
d = [float(x) for x in re.findall(r"-?\d+\.\d+", t.split("diffs ms:")[1].split("\n")[0])]
assert len(d) >= 10, "too few matched beeps"
before, after = d[:4], d[-5:]
assert max(abs(x) for x in before) <= 15, f"not in sync before the nudge: {before}"
assert all(abs(x - 120) <= 15 for x in after), f"nudge not applied: {after}"
print(f"PASS: timing nudge moves one speaker (before {before}, after {after})")
PY2
