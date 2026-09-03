/*
 * S7 spike harness: a controllable virtual keyboard via /dev/uinput.
 * Creates a genuine kernel input device, prints its sysfs event-node path,
 * then reads single-character commands from stdin:
 *   d = RightCtrl key-down
 *   u = RightCtrl key-up
 *   q = destroy device and exit
 * Throwaway spike code -- no error-handling investment beyond fail-loud,
 * matching this project's established spike convention (see spikes/s4-inject-win/README.md).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <linux/uinput.h>
#include <linux/input.h>
#include <sys/ioctl.h>

static void emit(int fd, int type, int code, int val) {
    struct input_event ev = {0};
    ev.type = type; ev.code = code; ev.value = val;
    write(fd, &ev, sizeof(ev));
}

int main(void) {
    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) { perror("open /dev/uinput"); return 1; }

    ioctl(fd, UI_SET_EVBIT, EV_KEY);
    ioctl(fd, UI_SET_KEYBIT, KEY_RIGHTCTRL);
    /* KeyboardDeviceFilter.IsKeyboardLike requires ALL 26 QWERTY alpha scancodes present
     * (distinguishes a real keyboard from a power-button/media-key node) -- register them
     * even though this harness never actually emits any of them. */
    int alpha[] = {
        KEY_Q, KEY_W, KEY_E, KEY_R, KEY_T, KEY_Y, KEY_U, KEY_I, KEY_O, KEY_P,
        KEY_A, KEY_S, KEY_D, KEY_F, KEY_G, KEY_H, KEY_J, KEY_K, KEY_L,
        KEY_Z, KEY_X, KEY_C, KEY_V, KEY_B, KEY_N, KEY_M
    };
    for (size_t i = 0; i < sizeof(alpha) / sizeof(alpha[0]); i++)
        ioctl(fd, UI_SET_KEYBIT, alpha[i]);

    struct uinput_setup usetup;
    memset(&usetup, 0, sizeof(usetup));
    usetup.id.bustype = BUS_USB;
    usetup.id.vendor = 0x1234;
    usetup.id.product = 0x5678;
    strcpy(usetup.name, "soneto-s7-virtual-keyboard");

    if (ioctl(fd, UI_DEV_SETUP, &usetup) < 0) { perror("UI_DEV_SETUP"); return 1; }
    if (ioctl(fd, UI_DEV_CREATE) < 0) { perror("UI_DEV_CREATE"); return 1; }

    usleep(300000); /* let the kernel finish registering the device */
    fprintf(stderr, "READY\n");
    fflush(stderr);

    char cmd;
    while (read(STDIN_FILENO, &cmd, 1) == 1) {
        if (cmd == 'd') {
            emit(fd, EV_KEY, KEY_RIGHTCTRL, 1);
            emit(fd, EV_SYN, SYN_REPORT, 0);
            fprintf(stderr, "DOWN-SENT\n"); fflush(stderr);
        } else if (cmd == 'u') {
            emit(fd, EV_KEY, KEY_RIGHTCTRL, 0);
            emit(fd, EV_SYN, SYN_REPORT, 0);
            fprintf(stderr, "UP-SENT\n"); fflush(stderr);
        } else if (cmd == 'q') {
            break;
        }
    }

    ioctl(fd, UI_DEV_DESTROY);
    close(fd);
    return 0;
}
