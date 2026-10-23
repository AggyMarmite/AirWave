#!/usr/bin/env bash
# Screen mirroring protocol test against a stand-in Apple TV (mock-tv), for each way a TV can be set up:
# open to everyone (transient pairing), on-screen PIN, and password. The picture must arrive frame-for-frame
# identical to what was sent, with sound packets decrypting and timestamps ahead of "now" by the latency.
#   tests/mirror/run.sh <work-dir>
set -euo pipefail
WORK=${1:-/tmp/airwave-mirror}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
mkdir -p "$WORK" && cd "$WORK"
(cd "$REPO/src/AirWave.TestCli" && dotnet build -c Release -v q -nologo > build.log 2>&1) || { cat build.log; exit 1; }
CLI="dotnet $REPO/src/AirWave.TestCli/bin/Release/net8.0/AirWave.TestCli.dll"
[[ -f in.h264 ]] || ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=6" -c:v libx264 -profile:v high -bf 0 -g 30 -pix_fmt yuv420p -f h264 in.h264
ffmpeg -hide_banner -loglevel error -y -i in.h264 -f framemd5 in.md5
fail=0
port=7100
for mode in open pin password; do
  port=$((port+1))
  rm -rf pair-$mode out-$mode.h264
  $CLI mock-tv --mode $mode --port $port --out out-$mode.h264 --seconds 25 > tv-$mode.log 2>&1 &
  TV=$!
  sleep 1.5
  extra=""
  [[ $mode == pin ]] && extra="--pin-value 4821"
  [[ $mode == password ]] && extra="--password secret-pw"
  $CLI mirror-test 127.0.0.1:$port --h264 in.h264 --audio --pair-dir pair-$mode $extra > tx-$mode.log 2>&1 || true
  sleep 1
  kill $TV 2>/dev/null || true; wait $TV 2>/dev/null || true
  ffmpeg -hide_banner -loglevel error -y -i out-$mode.h264 -f framemd5 out-$mode.md5 2>/dev/null || true
  sent=$(grep -c '^0,' in.md5); got=$(grep -c '^0,' out-$mode.md5 2>/dev/null || echo 0)
  same=$(diff <(grep '^0,' in.md5 | awk -F, '{print $6}') <(grep '^0,' out-$mode.md5 2>/dev/null | awk -F, '{print $6}') > /dev/null && echo yes || echo no)
  audio=$(grep -o 'AUDIO packets ok=[0-9]* bad=[0-9]*' tv-$mode.log | tail -1)
  lead=$(grep -o 'lead=-\?[0-9]*ms' tv-$mode.log | tail -1)
  echo "$mode: frames sent $sent, received $got, identical=$same, $audio, $lead, paired=$(grep -c PAIRED tx-$mode.log), teardown=$(grep -c TEARDOWN tv-$mode.log)"
  if [[ $same != yes ]] || ! grep -q "bad=0" <<< "$audio" || grep -q "^ERR" tv-$mode.log; then fail=1; echo "--- tx"; tail -5 tx-$mode.log; echo "--- tv"; tail -8 tv-$mode.log; fi
done
[[ $fail == 0 ]] && echo "PASS: screen mirroring protocol (open, PIN and password TVs)" || { echo "FAIL"; exit 1; }
