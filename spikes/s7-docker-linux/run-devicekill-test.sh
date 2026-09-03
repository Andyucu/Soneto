#!/usr/bin/env bash
# S7 spike, Phase 4 item 5 (§4.6) addition: DEVICE-KILL (not device-ADD/hotplug) recovery --
# the closer match to "Windows will unhook you eventually" for Linux. Creates keyboard #1,
# starts the real LinuxHotkeySource against it, confirms a normal press/release, then
# DESTROYS keyboard #1 mid-session (uinput_kbd.c's 'q' command: UI_DEV_DESTROY + close(fd)) --
# the closest a container can get to "the underlying device died/was unplugged out from under
# an active reader." A fresh keyboard #2 is created shortly after (during the watchdog's real
# backoff window) so a later restart attempt has something to recover ONTO, mirroring "the hook
# comes back eventually" on Windows. Confirms the real, unmodified LinuxHotkeySource genuinely
# detects the dead device and the harness's mirrored real-shaped watchdog (see Program.cs)
# recovers it, the same way WindowsHotkeySource/SessionController do.
# Throwaway spike script -- fail loud, no error-handling investment beyond that.
set -euo pipefail

mkdir -p /dev/input
gcc -O0 -o /tmp/uinput_kbd uinput_kbd.c

wait_for_new_event() {
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
    # Real finding (this script, not LinuxHotkeySource): after a device is destroyed, the
    # kernel can recycle the SAME eventN name for the next device it creates. A stale
    # /dev/input/$new node left over from a PREVIOUS device (destroyed but never rm'd) has
    # the OLD major:minor baked in -- opening it fails even though the name looks right.
    # Always rm -f then mknod fresh so the node's major:minor matches whatever is CURRENTLY
    # live, exactly what udev would do automatically on a real desktop.
    rm -f "/dev/input/$new"
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

# --- Confirm normal press/release on keyboard #1 works before killing anything ---
sleep 0.3
echo -n "d" >&3
sleep 0.15
echo -n "u" >&3
sleep 0.5
echo "report" >&4
sleep 0.3

# --- DESTROY keyboard #1 while LinuxHotkeySource's real reader thread is actively polling
#     its fd -- the real device-death scenario item 5 asks for, not a simulation of one. ---
echo "[SCRIPT] killing keyboard #1 (device destroy, not just process kill) ..."
echo -n "q" >&3
wait $K1_PID 2>/dev/null || true

# --- Create keyboard #2 ~2s later, DURING the watchdog's real backoff window (harness mirrors
#     SessionController's real 1s/2s/4s/8s/16s shape -- see Program.cs), so a later restart
#     attempt has a real device to recover onto -- mirroring "the hook eventually comes back". ---
sleep 2
BEFORE2=$(ls /sys/class/input/ 2>/dev/null | grep '^event' | sort || true)
mkfifo /tmp/kbd2_cmd
/tmp/uinput_kbd < /tmp/kbd2_cmd > /tmp/kbd2.out 2>/tmp/kbd2.err &
K2_PID=$!
exec 5>/tmp/kbd2_cmd
for i in $(seq 1 50); do grep -q READY /tmp/kbd2.err 2>/dev/null && break; sleep 0.1; done
EV2=$(wait_for_new_event "$BEFORE2")
echo "[SCRIPT] keyboard #2 (replacement) -> /dev/input/$EV2"

# --- Wait for the harness's real, mirrored-backoff watchdog to recover (up to the full
#     1+2+4+8=15s of backoff sleep across 4 failed attempts, plus attempt overhead). ---
for i in $(seq 1 40); do grep -q "RECOVERED:" /tmp/harness.out 2>/dev/null && break; sleep 0.5; done

# --- Confirm the hotkey genuinely works again, through the replacement device. ---
echo -n "d" >&5
sleep 0.15
echo -n "u" >&5
sleep 0.5
echo "report" >&4
sleep 0.5
echo "q" >&4

wait $H_PID 2>/dev/null || true
kill $K2_PID 2>/dev/null || true

echo "=================== HARNESS LOG ==================="
cat /tmp/harness.out
echo "====================================================="

if ! grep -q "RECOVERED:" /tmp/harness.out; then
    echo "[SCRIPT] FAIL: harness never reported RECOVERED after the device kill." >&2
    exit 1
fi
echo "[SCRIPT] PASS: real LinuxHotkeySource detected the killed device and recovered via the real-shaped watchdog."
