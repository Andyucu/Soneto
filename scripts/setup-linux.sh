#!/usr/bin/env bash
#
# scripts/setup-linux.sh -- Soneto Linux prerequisites, per plan §1.9:
#   "input group membership, the /dev/uinput udev rule, a ydotoold user systemd unit, and
#   a verification pass that prints red/green for each."
#
# Fedora/systemd/udev-focused (this project's stated target: Fedora KDE Wayland, spike S5).
# Should also work unmodified on most other systemd-based distros; the only distro-specific
# bit is the package-manager hint printed when ydotool/ydotoold isn't installed.
#
# HONESTY NOTE (read before trusting this script): this script has NEVER been executed --
# there is no Linux/systemd/udev environment available to the agent session that wrote it.
# It is written carefully against standard, documented Fedora/systemd/udev conventions
# (`usermod -aG`, /etc/udev/rules.d/, `systemctl --user`), not against an actual test run.
# Review it before running it on a real machine, and treat the verification pass at the end
# as the actual source of truth for what did/didn't work, not this comment.
#
# Usage: ./scripts/setup-linux.sh
#
set -uo pipefail

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m'

pass() { echo -e "  ${GREEN}✓${NC} $1"; }
fail() { echo -e "  ${RED}✗${NC} $1"; }
warn() { echo -e "  ${YELLOW}!${NC} $1"; }

echo "=== Soneto Linux setup ==="
echo

CURRENT_USER="${SUDO_USER:-$USER}"

# ---------------------------------------------------------------------------------------
# 1. 'input' group membership -- required to open /dev/input/event* nodes for evdev capture.
# ---------------------------------------------------------------------------------------
echo "--- 1. 'input' group membership for $CURRENT_USER ---"
if id -nG "$CURRENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx "input"; then
    echo "Already a member of 'input' -- skipping usermod (idempotent check)."
else
    echo "Adding $CURRENT_USER to the 'input' group (requires sudo)..."
    if sudo usermod -aG input "$CURRENT_USER"; then
        echo "Added. NOTE: this only takes effect on your NEXT LOGIN SESSION -- log out/in"
        echo "(or reboot) before expecting evdev device access to work."
    else
        echo "usermod failed -- see the error above."
    fi
fi
echo

# ---------------------------------------------------------------------------------------
# 2. /dev/uinput udev rule -- required for ydotoold to create a virtual input device.
# ---------------------------------------------------------------------------------------
echo "--- 2. /dev/uinput udev rule ---"
UDEV_RULE_FILE="/etc/udev/rules.d/60-soneto-uinput.rules"
UDEV_RULE_CONTENT='KERNEL=="uinput", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"'

if [ -f "$UDEV_RULE_FILE" ] && grep -qF "$UDEV_RULE_CONTENT" "$UDEV_RULE_FILE" 2>/dev/null; then
    echo "udev rule already present at $UDEV_RULE_FILE -- skipping."
else
    echo "Writing $UDEV_RULE_FILE (requires sudo)..."
    if echo "$UDEV_RULE_CONTENT" | sudo tee "$UDEV_RULE_FILE" > /dev/null; then
        echo "Reloading udev rules..."
        sudo udevadm control --reload-rules && sudo udevadm trigger
        echo "udev rule installed and reloaded."
    else
        echo "Failed to write the udev rule -- see the error above."
    fi
fi
echo

# ---------------------------------------------------------------------------------------
# 3. ydotoold user systemd unit.
# ---------------------------------------------------------------------------------------
echo "--- 3. ydotoold user systemd service ---"
if ! command -v ydotoold >/dev/null 2>&1; then
    warn "ydotool/ydotoold not found on PATH."
    if command -v dnf >/dev/null 2>&1; then
        echo "  Install with: sudo dnf install ydotool"
    elif command -v apt >/dev/null 2>&1; then
        echo "  Install with: sudo apt install ydotool"
    elif command -v pacman >/dev/null 2>&1; then
        echo "  Install with: sudo pacman -S ydotool"
    else
        echo "  Install ydotool via your distro's package manager, then re-run this script."
    fi
    echo "  Skipping systemd unit setup until ydotoold is installed."
else
    SYSTEMD_USER_DIR="$HOME/.config/systemd/user"
    UNIT_FILE="$SYSTEMD_USER_DIR/ydotoold.service"
    mkdir -p "$SYSTEMD_USER_DIR"

    cat > "$UNIT_FILE" <<'EOF'
[Unit]
Description=ydotoold - Soneto dictation paste/type backend
After=default.target

[Service]
ExecStart=/usr/bin/ydotoold
Restart=on-failure

[Install]
WantedBy=default.target
EOF
    echo "Wrote $UNIT_FILE"

    systemctl --user daemon-reload
    if systemctl --user enable --now ydotoold.service; then
        echo "ydotoold.service enabled and started."
    else
        echo "Failed to enable/start ydotoold.service -- see the error above (also check"
        echo "'systemctl --user status ydotoold.service' and 'journalctl --user -u ydotoold')."
    fi
fi
echo

# ---------------------------------------------------------------------------------------
# Verification pass.
# ---------------------------------------------------------------------------------------
echo "=== Verification ==="

if id -nG "$CURRENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx "input"; then
    pass "'input' group: $CURRENT_USER is a member (per /etc/group -- a NEW login session is"
    echo "    still required for this to apply to already-running processes/shells)."
else
    fail "'input' group: $CURRENT_USER is NOT a member."
fi

if [ -f "$UDEV_RULE_FILE" ] && grep -qF "$UDEV_RULE_CONTENT" "$UDEV_RULE_FILE" 2>/dev/null; then
    pass "udev rule: $UDEV_RULE_FILE present with the expected content."
else
    fail "udev rule: $UDEV_RULE_FILE missing or does not match the expected content."
fi

if [ -e /dev/uinput ]; then
    pass "/dev/uinput exists."
else
    warn "/dev/uinput does not exist yet -- this is expected until the uinput kernel module"
    echo "    is loaded (usually on first ydotoold start) or after a reboot."
fi

if command -v ydotoold >/dev/null 2>&1; then
    pass "ydotoold binary found on PATH."
    if systemctl --user is-active --quiet ydotoold.service 2>/dev/null; then
        pass "ydotoold.service is active."
    else
        fail "ydotoold.service is not active (see 'systemctl --user status ydotoold.service')."
    fi
else
    fail "ydotoold binary not found -- install it (see step 3 above) and re-run this script."
fi

if command -v wl-copy >/dev/null 2>&1 || command -v xclip >/dev/null 2>&1; then
    pass "A clipboard tool is available ($(command -v wl-copy 2>/dev/null || command -v xclip))."
else
    fail "Neither wl-copy nor xclip found -- install 'wl-clipboard' (Wayland) or 'xclip' (X11)."
fi

echo
echo "If any 'input' group check shows a red X above, or you just ran this script for the"
echo "first time, LOG OUT AND BACK IN (or reboot), then re-run this script to confirm green."
