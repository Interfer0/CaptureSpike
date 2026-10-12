/*
 * cs-pw-helper: xdg-desktop-portal + PipeWire screen capture helper
 *
 * This program is the backend for Wayland screen capture in
 * CaptureSpike. It:
 *
 *   1. Calls xdg-desktop-portal's ScreenCast API via D-Bus
 *      to ask the user which source to share (full screen,
 *      a window, or a region). The user sees a system
 *      dialog and grants permission.
 *   2. The portal returns a PipeWire node ID + file
 *      descriptor. We connect to PipeWire and start pulling
 *      frames from that node.
 *   3. We write the frames to a Unix socket in the rawvideo
 *      format ffmpeg can consume.
 *
 * ffmpeg command line (consumed by the C# layer):
 *   ffmpeg -f rawvideo -pix_fmt bgra -video_size WxH
 *          -framerate N -i /path/to/socket ...
 *
 * The C helper writes the rawvideo header (a fixed
 * binary record) once when the stream starts, then
 * streams frames. ffmpeg's rawvideo demuxer reads the
 * header from the start of the file.
 *
 * Protocol on the Unix socket:
 *   - One "stream header" written at start:
 *       4 bytes  magic "CSWH"
 *       4 bytes  version (1)
 *       4 bytes  width  (LE)
 *       4 bytes  height (LE)
 *       4 bytes  format code (1 = BGRA, 2 = BGR0)
 *       4 bytes  framerate numerator (LE)
 *       4 bytes  framerate denominator (LE)
 *     Total: 32 bytes
 *   - Then per-frame:
 *       4 bytes  stride (LE) -- == width * 4 for BGRA
 *       4 bytes  flags (LE)  -- 0 = keyframe, 1 = end of stream
 *       N bytes  pixel data
 *
 * PipeWire interaction uses libpipewire. Portal
 * interaction uses libportal (the C library that wraps
 * the org.freedesktop.portal.ScreenCast D-Bus API).
 *
 * Build:
 *   sudo apt install libpipewire-0.3-dev libportal-dev
 *   make
 *
 * Run:
 *   cs-pw-helper --socket /tmp/cs-pw.sock --cursor \
 *                 --source-type 1 --framerate 30
 *
 * Exit codes:
 *   0   - clean exit
 *   1   - invalid arguments
 *   2   - portal D-Bus call failed
 *   3   - PipeWire connection failed
 *   4   - stream error during capture
 *   5   - user cancelled the permission dialog
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <signal.h>
#include <getopt.h>

#include <sys/socket.h>
#include <sys/un.h>

#include <libportal/portal.h>
#include <libportal-gtk3/portal-gtk3.h>

#include <pipewire/pipewire.h>
#include <pipewire/stream.h>
#include <spa/param/video/format-utils.h>

/* Raw video format codes (must match the C# side's
 * RawVideoFormat enum). */
#define CSWH_MAGIC     "CSWH"
#define CSWH_VERSION   1
#define CSWH_FORMAT_BGRA 1
#define CSWH_FORMAT_BGR0 2

#define EXIT_OK              0
#define EXIT_BAD_ARGS        1
#define EXIT_PORTAL_FAILED   2
#define EXIT_PIPEWIRE_FAILED 3
#define EXIT_STREAM_ERROR    4
#define EXIT_USER_CANCELLED  5

/* Source types (org.freedesktop.portal.ScreenCast.SourceType).
 * Must match libportal's XdpScreenCastSourceType enum values. */
#define SOURCE_TYPE_MONITOR 1
#define SOURCE_TYPE_WINDOW  2
#define SOURCE_TYPE_REGION  4

/* Globals. Cleaned up in main()'s cleanup path. */
static volatile sig_atomic_t g_interrupted = 0;
static struct pw_main_loop *g_loop = NULL;
static struct pw_stream *g_stream = NULL;
static int g_socket_fd = -1;
static int g_frame_width = 0;
static int g_frame_height = 0;
static int g_frame_format = 0;
static int g_frame_stride = 0;
static int g_framerate_num = 30;
static int g_framerate_den = 1;

static void on_sigint(int sig) {
    (void)sig;
    g_interrupted = 1;
    if (g_loop) pw_main_loop_quit(g_loop);
}

/* ---- Socket protocol ---- */

static int write_all(int fd, const void *buf, size_t len) {
    const char *p = buf;
    while (len > 0) {
        ssize_t n = write(fd, p, len);
        if (n < 0) {
            if (errno == EINTR) continue;
            return -1;
        }
        p += n;
        len -= n;
    }
    return 0;
}

static int write_stream_header(void) {
    unsigned char hdr[32];
    memcpy(hdr, CSWH_MAGIC, 4);
    uint32_t v = CSWH_VERSION, w = g_frame_width, h = g_frame_height;
    uint32_t fmt = g_frame_format, n = g_framerate_num, d = g_framerate_den;
    memcpy(hdr + 4,  &v, 4);
    memcpy(hdr + 8,  &w, 4);
    memcpy(hdr + 12, &h, 4);
    memcpy(hdr + 16, &fmt, 4);
    memcpy(hdr + 20, &n, 4);
    memcpy(hdr + 24, &d, 4);
    memcpy(hdr + 28, "\0\0\0\0", 4); /* reserved */
    return write_all(g_socket_fd, hdr, sizeof(hdr));
}

static int write_frame(const void *pixels, size_t stride, size_t size) {
    unsigned char meta[8];
    uint32_t s = (uint32_t)stride, flags = 0;
    memcpy(meta, &s, 4);
    memcpy(meta + 4, &flags, 4);
    if (write_all(g_socket_fd, meta, 8) < 0) return -1;
    return write_all(g_socket_fd, pixels, size);
}

static int write_end_of_stream(void) {
    unsigned char meta[8] = {0};
    uint32_t flags = 1;
    memcpy(meta + 4, &flags, 4);
    return write_all(g_socket_fd, meta, 8);
}

/* ---- Unix socket setup ---- */

static int create_unix_socket(const char *path) {
    /* Unlink any stale socket. */
    unlink(path);
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) {
        fprintf(stderr, "cs-pw-helper: socket() failed: %s\n", strerror(errno));
        return -1;
    }
    struct sockaddr_un addr = {0};
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, path, sizeof(addr.sun_path) - 1);
    if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        fprintf(stderr, "cs-pw-helper: bind(%s) failed: %s\n", path, strerror(errno));
        close(fd);
        return -1;
    }
    if (listen(fd, 1) < 0) {
        fprintf(stderr, "cs-pw-helper: listen() failed: %s\n", strerror(errno));
        close(fd);
        return -1;
    }
    return fd;
}

static int accept_client(int listen_fd) {
    fprintf(stderr, "cs-pw-helper: waiting for ffmpeg to connect to the socket...\n");
    int fd = accept(listen_fd, NULL, NULL);
    if (fd < 0) {
        fprintf(stderr, "cs-pw-helper: accept() failed: %s\n", strerror(errno));
        return -1;
    }
    fprintf(stderr, "cs-pw-helper: ffmpeg connected.\n");
    return fd;
}

/* ---- PipeWire stream callbacks ---- */

static void on_stream_state_changed(void *data, enum pw_stream_state old, enum pw_stream_state new, const char *err) {
    (void)data; (void)old;
    fprintf(stderr, "cs-pw-helper: stream state %d -> %d (%s)\n", old, new, err ? err : "ok");
    if (new == PW_STREAM_STATE_ERROR || new == PW_STREAM_STATE_UNCONNECTED) {
        pw_main_loop_quit(g_loop);
    }
}

static void on_stream_param_changed(void *data, uint32_t id, const struct spa_pod *param) {
    (void)data; (void)id;
    if (param == NULL) return;
    if (id != SPA_PARAM_Format) return;

    struct spa_video_info_raw fmt = {0};
    if (spa_format_video_raw_parse(param, &fmt) < 0) {
        fprintf(stderr, "cs-pw-helper: failed to parse video format\n");
        return;
    }
    g_frame_width = fmt.size.width;
    g_frame_height = fmt.size.height;
    g_framerate_num = fmt.framerate.num;
    g_framerate_den = fmt.framerate.den;

    /* Map SPA pixel format to our wire format code. */
    switch (fmt.format) {
        case SPA_VIDEO_FORMAT_BGRA: g_frame_format = CSWH_FORMAT_BGRA; break;
        case SPA_VIDEO_FORMAT_BGRx:
        case SPA_VIDEO_FORMAT_BGR0: g_frame_format = CSWH_FORMAT_BGR0; break;
        default:
            fprintf(stderr, "cs-pw-helper: unsupported pixel format %d, trying BGRA\n", fmt.format);
            g_frame_format = CSWH_FORMAT_BGRA;
    }
    g_frame_stride = g_frame_width * 4; /* BGRA = 4 bytes/pixel */

    fprintf(stderr, "cs-pw-helper: video %dx%d @ %d/%d fps, format code %d\n",
        g_frame_width, g_frame_height, g_framerate_num, g_framerate_den, g_frame_format);
}

static void on_stream_process(void *data) {
    (void)data;
    struct pw_buffer *buf;
    while ((buf = pw_stream_dequeue_buffer(g_stream)) != NULL) {
        struct spa_buffer *sbuf = buf->buffer;
        struct spa_data *d = &sbuf->datas[0];
        if (d->data == NULL || d->size == 0) {
            pw_stream_queue_buffer(g_stream, buf);
            continue;
        }
        if (write_frame(d->data, g_frame_stride, d->size) < 0) {
            fprintf(stderr, "cs-pw-helper: write to socket failed: %s\n", strerror(errno));
            pw_stream_queue_buffer(g_stream, buf);
            pw_main_loop_quit(g_loop);
            return;
        }
        pw_stream_queue_buffer(g_stream, buf);
    }
}

static const struct pw_stream_events stream_events = {
    .version = PW_VERSION_STREAM_EVENTS,
    .state_changed = on_stream_state_changed,
    .param_changed = on_stream_param_changed,
    .process = on_stream_process,
};

/* ---- main() ---- */

static void usage(const char *argv0) {
    fprintf(stderr, "Usage: %s [OPTIONS]\n"
        "\n"
        "  --socket PATH     Unix socket path to write frames to (required)\n"
        "  --source-type N   1 = monitor, 2 = window, 4 = region (default: 1)\n"
        "  --framerate N     Desired framerate (default: 30)\n"
        "  --cursor          Include the mouse cursor in the capture\n"
        "  --restore-token F Restore token from a previous session (advanced)\n"
        "  -h, --help        Show this help\n",
        argv0);
}

int main(int argc, char *argv[]) {
    const char *socket_path = NULL;
    int source_type = SOURCE_TYPE_MONITOR;
    int framerate = 30;
    int include_cursor = 0;
    const char *restore_token = NULL;

    static struct option long_opts[] = {
        {"socket",      required_argument, 0, 's'},
        {"source-type", required_argument, 0, 't'},
        {"framerate",   required_argument, 0, 'f'},
        {"cursor",      no_argument,       0, 'c'},
        {"restore-token", required_argument, 0, 'r'},
        {"help",        no_argument,       0, 'h'},
        {0, 0, 0, 0}
    };
    int opt;
    while ((opt = getopt_long(argc, argv, "s:t:f:r:ch", long_opts, NULL)) != -1) {
        switch (opt) {
            case 's': socket_path = optarg; break;
            case 't': source_type = atoi(optarg); break;
            case 'f': framerate = atoi(optarg); break;
            case 'c': include_cursor = 1; break;
            case 'r': restore_token = optarg; break;
            case 'h': usage(argv[0]); return EXIT_OK;
            default:  usage(argv[0]); return EXIT_BAD_ARGS;
        }
    }
    if (!socket_path) {
        usage(argv[0]);
        return EXIT_BAD_ARGS;
    }
    if (source_type != SOURCE_TYPE_MONITOR && source_type != SOURCE_TYPE_WINDOW && source_type != SOURCE_TYPE_REGION) {
        fprintf(stderr, "cs-pw-helper: invalid --source-type %d\n", source_type);
        return EXIT_BAD_ARGS;
    }

    signal(SIGINT, on_sigint);
    signal(SIGTERM, on_sigint);
    signal(SIGPIPE, SIG_IGN);

    g_framerate_num = framerate;
    g_framerate_den = 1;

    /* ---- Portal: request screen capture ---- */
    XdpPortal *portal = xdp_portal_new_with_gtk3();
    if (!portal) {
        fprintf(stderr, "cs-pw-helper: xdp_portal_new_with_gtk3 failed (no GTK?)\n");
        return EXIT_PORTAL_FAILED;
    }

    GVariant *restore_token_variant = NULL;
    if (restore_token) {
        restore_token_variant = g_variant_new_string(restore_token);
    }

    /* Prepare the options. */
    GVariantBuilder options = G_VARIANT_BUILDER_INIT(G_VARIANT_TYPE_VARDICT);
    g_variant_builder_add(&options, "{sv}", "multiple",
        g_variant_new_boolean(FALSE));
    g_variant_builder_add(&options, "{sv}", "cursor_mode",
        g_variant_new_uint32(include_cursor ? 2 : 1)); /* 1 = hidden, 2 = embedded */
    g_variant_builder_add(&options, "{sv}", "types",
        g_variant_new_uint32(source_type));

    /* Session: ask the user to pick a source. */
    XdpSession *session = xdp_portal_create_screencast_session(
        portal,
        NULL, /* parent window XID, NULL = no parent */
        XDP_OUTPUT_MONITOR,
        XDP_SELECTION_CHOOSE,
        restore_token_variant,
        NULL, /* cancellable */
        NULL, NULL  /* GAsyncReadyCallback, user_data */);
    /* NOTE: the above is a placeholder — xdp_portal_create_screencast_session
     * doesn't return a session synchronously; the real API is
     * xdp_portal_create_screencast_session_full which takes a
     * callback. The C# layer needs to adapt to the async
     * D-Bus pattern. The "session" call is fundamentally
     * async; we can't block on it. */
    if (!session) {
        fprintf(stderr, "cs-pw-helper: failed to create screencast session\n");
        g_object_unref(portal);
        return EXIT_PORTAL_FAILED;
    }
    /* TODO: hook up the actual async callbacks and continue
     * the flow in a state machine. This is the real work. */
    fprintf(stderr, "cs-pw-helper: portal skeleton compiled, async flow not yet wired up.\n");

    g_object_unref(session);
    g_object_unref(portal);
    return EXIT_PORTAL_FAILED;
}
