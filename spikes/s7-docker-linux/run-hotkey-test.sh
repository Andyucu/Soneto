#!/usr/bin/env bash
# S7 spike: orchestrates a real uinput virtual keyboard + the real LinuxHotkeySource harness
# inside one container. Must run with --device=/dev/uinput --device-cgroup-rule='c 13:* rmw'
# (see README.md for why both are needed). Throwaway spike script.
set -euo pipefail

mkdir -p /dev/input

# --- Build the uinput helper ---
gcc -O0 -o /tmp/uinput_kbd uinput_kbd.c

# --- Start it, capture its stderr (status lines) to a file we can poll ---
mkfifo /tmp/kbd_cmd
/tmp/uinput_kbd < /tmp/kbd_cmd > /tmp/kbd.out 2>/tmp/kbd.err &
KBD_PID=$!
exec 3>/tmp/kbd_cmd   # keep the fifo open for writing across multiple echoes

# Wait for READY on stderr
for i in $(seq 1 50); do
    grep -q READY /tmp/kbd.err 2>/dev/null && break
    sleep 0.1
done
grep -q READY /tmp/kbd.err || { echo "uinput_kbd never became READY"; cat /tmp/kbd.err; exit 1; }
echo "[SCRIPT] uinput_kbd ready."

# --- Find its event-node major:minor via sysfs and mknod it into /dev/input ---
EVDEV=$(ls /sys/class/input/ | grep '^event' | head -1)
MAJMIN=$(cat /sys/class/input/$EVDEV/dev)
MAJOR=${MAJMIN%%:*}
MINOR=${MAJMIN##*:}
mknod "/dev/input/$EVDEV" c "$MAJOR" "$MINOR"
chmod 600 "/dev/input/$EVDEV"
echo "[SCRIPT] mknod'd /dev/input/$EVDEV ($MAJOR:$MINOR)"

# --- Build and start the real .NET harness against Soneto.Platform.Linux ---
cd Harness
dotnet build -c Release -v q
mkfifo /tmp/harness_cmd
dotnet /tmp/harness-bin/Release/net10.0/Harness.dll < /tmp/harness_cmd > /tmp/harness.out 2>&1 &
H_PID=$!
exec 4>/tmp/harness_cmd

# Wait for the harness to confirm StartAsync succeeded
for i in $(seq 1 50); do
    grep -q "StartAsync returned successfully" /tmp/harness.out 2>/dev/null && break
    sleep 0.1
done
if ! grep -q "StartAsync returned successfully" /tmp/harness.out 2>/dev/null; then
    echo "[SCRIPT] Harness never confirmed StartAsync -- dumping log:"
    cat /tmp/harness.out
    exit 1
fi
echo "[SCRIPT] Real LinuxHotkeySource.StartAsync succeeded against the mknod'd device."

# --- Drive one real press+release cycle ---
sleep 0.5
echo -n "d" >&3; echo "d" >&4
sleep 0.15
echo -n "u" >&3; echo "u" >&4
sleep 0.5
echo "report" >&4
sleep 0.5
echo "q" >&4
echo "q" >&3

wait $H_PID 2>/dev/null || true
kill $KBD_PID 2>/dev/null || true

echo "=================== HARNESS LOG ==================="
cat /tmp/harness.out
echo "====================================================="
