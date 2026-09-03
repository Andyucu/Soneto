#!/usr/bin/env bash
# S7 spike: multi-keyboard hotplug -- creates keyboard #1, starts the real LinuxHotkeySource
# against it, confirms a normal press/release, THEN creates keyboard #2 mid-session (the
# closest a container can get to "plug in a second keyboard") and confirms the real inotify
# watch fires the hotplug-shaped fault and RestartAsync successfully re-enumerates.
# Throwaway spike script.
set -euo pipefail

mkdir -p /dev/input
gcc -O0 -o /tmp/uinput_kbd uinput_kbd.c

wait_for_new_event() {
    # $1 = newline-separated list of event names to ignore (already known before this call)
    local before="$1" after new
    for i in $(seq 1 30); do
        after=$(ls /sys/class/input/ 2>/dev/null | grep '^event' | sort || true)
        new=$(comm -13 <(echo "$before") <(echo "$after"))
        [ -n "$new" ] && break
        sleep 0.1
    done
    [ -n "$new" ] || { echo "no new event node appeared" >&2; exit 1; }
    local majmin
    majmin=$(cat "/sys/class/input/$new/dev")
    mknod "/dev/input/$new" c "${majmin%%:*}" "${majmin##*:}"
    chmod 600 "/dev/input/$new"
    echo "$new"
}

# --- Keyboard #1 ---
BEFORE1=$(ls /sys/class/input/ 2>/dev/null | grep '^event' | sort || true)
mkfifo /tmp/kbd1_cmd
/tmp/uinput_kbd < /tmp/kbd1_cmd > /tmp/kbd1.out 2>/tmp/kbd1.err &
K1_PID=$!
exec 3>/tmp/kbd1_cmd
for i in $(seq 1 50); do grep -q READY /tmp/kbd1.err 2>/dev/null && break; sleep 0.1; done
EV1=$(wait_for_new_event "$BEFORE1")
echo "[SCRIPT] keyboard #1 -> /dev/input/$EV1"

# --- Start the real harness against keyboard #1 ---
cd Harness
dotnet build -c Release -v q
mkfifo /tmp/harness_cmd
dotnet /tmp/harness-bin/Release/net10.0/Harness.dll < /tmp/harness_cmd > /tmp/harness.out 2>&1 &
H_PID=$!
exec 4>/tmp/harness_cmd
for i in $(seq 1 50); do grep -q "StartAsync returned successfully" /tmp/harness.out 2>/dev/null && break; sleep 0.1; done
echo "[SCRIPT] harness started against keyboard #1."
cd ..

# --- Confirm normal press/release on keyboard #1 works before touching anything ---
sleep 0.3
echo -n "d" >&3
sleep 0.15
echo -n "u" >&3
sleep 0.5
echo "report" >&4
sleep 0.3

# --- Now create keyboard #2 mid-session: the closest thing to real physical hotplug this
#     container can produce -- a genuinely new /dev/input/eventN node appearing while
#     LinuxHotkeySource's real inotify watch on /dev/input is live. ---
BEFORE2=$(ls /sys/class/input/ 2>/dev/null | grep '^event' | sort || true)
mkfifo /tmp/kbd2_cmd
/tmp/uinput_kbd < /tmp/kbd2_cmd > /tmp/kbd2.out 2>/tmp/kbd2.err &
K2_PID=$!
exec 5>/tmp/kbd2_cmd
for i in $(seq 1 50); do grep -q READY /tmp/kbd2.err 2>/dev/null && break; sleep 0.1; done
EV2=$(wait_for_new_event "$BEFORE2")
echo "[SCRIPT] keyboard #2 (hotplug) -> /dev/input/$EV2"

# Give the reader thread's inotify watch + the harness's auto-RestartAsync time to run
sleep 3

# --- Confirm the hotkey still works after the restart, sending through EITHER keyboard,
#     since after RestartAsync re-enumerates, both are real keyboard-like devices now. ---
echo -n "d" >&3
sleep 0.15
echo -n "u" >&3
sleep 0.5
echo "report" >&4
sleep 0.5
echo "q" >&4

wait $H_PID 2>/dev/null || true
kill $K1_PID $K2_PID 2>/dev/null || true

echo "=================== HARNESS LOG ==================="
cat /tmp/harness.out
echo "====================================================="
