#!/usr/bin/env bash
# Relay test: a sender plays to AirWave's receiver, which passes the audio on to Shairport Sync.
# Checks the audio arrives at Shairport Sync intact with a steady delay.
#   sudo tests/e2e/relay.sh <work-dir>
set -euo pipefail
WORK=${1:-/tmp/airwave-e2e}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
cd "$WORK"
SPS=$WORK/shairport-sync/shairport-sync
NQPTP=$WORK/nqptp/nqptp
(cd "$REPO/src/AirWave.TestCli" && dotnet build -c Release -v q -nologo > "$WORK/build.log" 2>&1) || { cat "$WORK/build.log"; exit 1; }
CLI="dotnet $REPO/src/AirWave.TestCli/bin/Release/net8.0/AirWave.TestCli.dll"
sysctl -q -w net.ipv4.ip_forward=1
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
  ip netns exec $ns ip route add default via 10.9$i.0.1
done
rm -f rx1.pcm && mkfifo rx1.pcm
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
cleanup() { for i in 1 2; do for p in $(ip netns pids awrx$i 2>/dev/null); do kill $p 2>/dev/null || true; done; ip netns del awrx$i 2>/dev/null || true; done; }
trap cleanup EXIT
for t in $(seq 1 60); do ip netns exec awrx1 ss -lnt | grep -q ':7000' && break; sleep 0.25; done
(ip netns exec awrx2 $CLI receive --name "E2E-AirWave" --port 7000 --id-dir $WORK/rxid --relay 10.91.0.2:7000 --seconds 40 --debug > $WORK/airwave-relay.log 2>&1 &)
for t in $(seq 1 60); do grep -q "RELAY" $WORK/airwave-relay.log 2>/dev/null && break; sleep 0.25; done
grep RELAY $WORK/airwave-relay.log
[[ -f beeps.adts ]] || ffmpeg -hide_banner -loglevel error -y -f lavfi -i "aevalsrc=0.25*sin(2*PI*440*t)*lt(mod(t\\,1)\\,0.1):s=44100:c=stereo:d=20" -c:a aac -b:a 256k -f adts beeps.adts
python3 "$HERE/sync_measure.py" 20 "$WORK/rx1.pcm" "$WORK/rx1.pcm" > "$WORK/relay-measure.txt" 2>/dev/null &
MPID=$!
sleep 0.5
$CLI send-buffered 10.92.0.2:7000 --aac "$WORK/beeps.adts" --seconds 14 > "$WORK/play.txt"
wait $MPID || true
python3 - "$WORK/relay-measure.txt" "$WORK/play.txt" <<'PY'
import sys, re
t = open(sys.argv[1]).read(); p = open(sys.argv[2]).read()
anchor = float(re.search(r"ANCHOR monotonic ([\d.]+)", p).group(1))
on = [float(x) for x in re.search(r"rx1 onsets: (.*)", t).group(1).split()]
lat = [round((o - anchor - 1024 / 44100) % 1.0, 3) for o in on]
print("relayed beeps:", len(on), "extra delay mod 1 s:", lat)
assert len(on) >= 8, "relayed audio didn't arrive"
spread = max(lat) - min(lat)
assert spread < 0.02, f"relay delay unsteady ({spread * 1000:.0f} ms spread)"
print(f"PASS: relay works, steady extra delay (spread {spread * 1000:.1f} ms)")
PY
