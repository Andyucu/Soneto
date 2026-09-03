#!/usr/bin/env bash
set -euo pipefail
mkdir -p /dev/input
gcc -O0 -o /tmp/uinput_kbd uinput_kbd.c
mkfifo /tmp/kbd_cmd
/tmp/uinput_kbd < /tmp/kbd_cmd > /tmp/kbd.out 2>/tmp/kbd.err &
KBD_PID=$!
exec 3>/tmp/kbd_cmd
for i in $(seq 1 50); do grep -q READY /tmp/kbd.err 2>/dev/null && break; sleep 0.1; done
EVDEV=$(ls /sys/class/input/ | grep '^event' | head -1)
MAJMIN=$(cat /sys/class/input/$EVDEV/dev)
mknod "/dev/input/$EVDEV" c "${MAJMIN%%:*}" "${MAJMIN##*:}"
chmod 600 "/dev/input/$EVDEV"
cd Harness
dotnet build -c Release -v q
mkfifo /tmp/harness_cmd
dotnet /tmp/harness-bin/Release/net10.0/Harness.dll < /tmp/harness_cmd > /tmp/harness.out 2>&1 &
H_PID=$!
exec 4>/tmp/harness_cmd
for i in $(seq 1 50); do grep -q "StartAsync returned successfully" /tmp/harness.out 2>/dev/null && break; sleep 0.1; done
echo "[SCRIPT] started; now idling 8s with device still alive, no dispose, no destroy, watching for a spontaneous fault..."
sleep 8
echo "report" >&4
sleep 1
cat /tmp/harness.out
kill $H_PID 2>/dev/null || true
kill $KBD_PID 2>/dev/null || true
