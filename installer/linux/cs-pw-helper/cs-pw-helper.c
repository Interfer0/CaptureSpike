/*
 * cs-pw-helper: xdg-desktop-portal + PipeWire screen capture helper
 *
 * Talks to the xdg-desktop-portal ScreenCast interface to get the
 * user's permission, opens a PipeWire connection to the resulting
 * remote, and writes the raw BGRA/BGR0 frames to a Unix socket that
 * ffmpeg can read as a rawvideo input.
 *
 * Flow:
 *   1. xdp_portal_create_screencast_session()    - async, returns XdpSession
 *   2. xdp_session_start()                        - async, user clicks Share
 *   3. xdp_session_open_pipewire_remote()         - sync, returns fd
 *   4. pw_context_connect_fd()                    - connect to PipeWire
 *   5. xdp_session_get_streams(session)           - get node IDs
 *   6. pw_stream_new()                            - create our stream
 *   7. pw_stream_connect() with node ID           - link to the source
 *   8. on_stream_process()                        - write to socket
 *
 * Compile:
 *   sudo apt install libpipewire-0.3-dev libportal-dev
 *   make
 *
 * Run:
 *   cs-pw-helper --socket /tmp/cs-pw.sock --source-type 1 --framerate 30
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
#include <pthread.h>
#include <getopt.h>

#include <sys/socket.h>
#include <sys/un.h>

#include <glib.h>
#include <gio/gio.h>
#include <libportal/portal.h>
#include <libportal/portal-enums.h>

#include <pipewire/pipewire.h>
#include <pipewire/stream.h>
#include <spa/param/video/format-utils.h>
#include <spa/utils/result.h>

#define SOURCE_TYPE_MONITOR 1
#define SOURCE_TYPE_WINDOW  2
#define SOURCE_TYPE_VIRTUAL 4

#define EXIT_OK              0
#define EXIT_BAD_ARGS        1
#define EXIT_PORTAL_FAILED   2
#define EXIT_PIPEWIRE_FAILED 3
#define EXIT_STREAM_ERROR    4
#define EXIT_USER_CANCELLED  5

static volatile sig_atomic_t g_interrupted = 0;
static GMainLoop *g_main_loop = NULL;
static XdpPortal *g_portal = NULL;
static XdpSession *g_session = NULL;

static const char *g_socket_path = NULL;
static int g_listen_fd = -1;
static int g_client_fd = -1;

static struct pw_main_loop *g_pw_main_loop = NULL;
static struct pw_loop *g_pw_loop = NULL;
static struct pw_context *g_pw_context = NULL;
static struct pw_core *g_pw_core = NULL;
static struct pw_stream *g_pw_stream = NULL;
static pthread_t g_pw_thread;
static int g_pw_thread_started = 0;

static int g_frame_width = 0;
static int g_frame_height = 0;
static int g_frame_format = 0;
static int g_frame_stride = 0;
static int g_framerate_num = 30;
static int g_framerate_den = 1;
static uint32_t g_pw_node_id = 0;

static struct spa_hook g_remote_listener;
static struct spa_hook g_stream_listener;
static struct spa_source *g_pw_event_source = NULL;

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

static void on_sigint(int sig) {
    (void)sig;
    g_interrupted = 1;
    if (g_main_loop) g_main_loop_quit(g_main_loop);
    g_interrupted = 1;
}

static void emit_header_line(int width, int height, int fps_num, int fps_den) {
    double fps = fps_den > 0 ? (double)fps_num / (double)fps_den : 0.0;
    fprintf(stderr, "CSWH W=%d H=%d FPS=%.2f\n", width, height, fps);
    fflush(stderr);
}

static int create_unix_socket(const char *path) {
    unlink(path);
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) { fprintf(stderr, "cs-pw-helper: socket() failed: %s\n", strerror(errno)); return -1; }
    struct sockaddr_un addr = {0};
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, path, sizeof(addr.sun_path) - 1);
    if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        fprintf(stderr, "cs-pw-helper: bind(%s) failed: %s\n", path, strerror(errno)); close(fd); return -1; }
    if (listen(fd, 1) < 0) {
        fprintf(stderr, "cs-pw-helper: listen() failed: %s\n", strerror(errno)); close(fd); return -1; }
    return fd;
}

static gboolean on_accept_client(GIOChannel *source, GIOCondition condition, gpointer user_data) {
    (void)source; (void)condition; (void)user_data;
    int fd = accept(g_listen_fd, NULL, NULL);
    if (fd < 0) {
        fprintf(stderr, "cs-pw-helper: accept() failed: %s\n", strerror(errno));
        return TRUE;
    }
    g_client_fd = fd;
    fprintf(stderr, "cs-pw-helper: ffmpeg connected to socket.\n");
    return FALSE;
}

static void on_stream_state_changed(void *data,
        enum pw_stream_state old, enum pw_stream_state new, const char *err) {
    (void)data; (void)old;
    fprintf(stderr, "cs-pw-helper: stream state %d -> %d (%s)\n", old, new, err ? err : "ok");
    if (new == PW_STREAM_STATE_ERROR || new == PW_STREAM_STATE_UNCONNECTED) {
        g_interrupted = 1;
    }
    /* Fallback: if we transitioned to STREAMING but
     * on_stream_param_changed never set our format
     * (e.g. the source accepted the format without
     * firing a param event), emit a default CSWH so
     * the C# side can start ffmpeg. The default is
     * 1920x1080 BGRA at the requested framerate. */
    if (new == PW_STREAM_STATE_STREAMING && g_frame_width == 0) {
        fprintf(stderr,
            "cs-pw-helper: WARNING: stream is up but no format "
            "negotiated; emitting fallback CSWH %dx%d BGRA @ %d/%d\n",
            1920, 1080, g_framerate_num, g_framerate_den);
        g_frame_width = 1920;
        g_frame_height = 1080;
        g_frame_format = SPA_VIDEO_FORMAT_BGRA;
        g_frame_stride = 1920 * 4;
        emit_header_line(g_frame_width, g_frame_height, g_framerate_num, g_framerate_den);
    }
}

static void on_stream_param_changed(void *data, uint32_t id, const struct spa_pod *param) {
    (void)data; (void)id;
    fprintf(stderr, "cs-pw-helper: on_stream_param_changed id=%u param=%p\n", id, (void*)param);
    /* The stream may send multiple param events:
     *   - SPA_PARAM_Format (3): the negotiated format
     *   - SPA_PARAM_Buffers (5): the buffer pool config
     *   - SPA_PARAM_Latency (6): latency info
     * We only care about Format. The rest are bookkeeping. */
    if (param == NULL) return;
    struct spa_video_info_raw fmt = {0};
    if (spa_format_video_raw_parse(param, &fmt) < 0) {
        fprintf(stderr, "cs-pw-helper: failed to parse video format\n");
        return;
    }
    g_frame_width = fmt.size.width;
    g_frame_height = fmt.size.height;
    g_framerate_num = fmt.framerate.num;
    g_framerate_den = fmt.framerate.denom;
    switch (fmt.format) {
        case SPA_VIDEO_FORMAT_BGRA: g_frame_format = SPA_VIDEO_FORMAT_BGRA; break;
        case SPA_VIDEO_FORMAT_BGRx:
        case SPA_VIDEO_FORMAT_BGR: g_frame_format = SPA_VIDEO_FORMAT_BGRx; break;
        default:
            fprintf(stderr, "cs-pw-helper: unsupported pixel format %d (will accept but not parse)\n", fmt.format);
            g_frame_format = fmt.format;
            break;
    }
    g_frame_stride = g_frame_width * 4;
    fprintf(stderr, "cs-pw-helper: video %dx%d @ %d/%d fps, format %d\n",
        g_frame_width, g_frame_height, g_framerate_num, g_framerate_den, g_frame_format);
    emit_header_line(g_frame_width, g_frame_height, g_framerate_num, g_framerate_den);

    /* After format negotiation, send buffer configuration
     * back to PipeWire so it knows how to allocate the
     * buffer pool. Without this, pw_stream_dequeue_buffer
     * never returns a buffer. */
    {
        uint8_t buf[1024];
        struct spa_pod_builder b = {0};
        spa_pod_builder_init(&b, buf, sizeof(buf));
        const struct spa_pod *params[1];
        uint32_t n_params = 0;
        params[n_params++] = spa_pod_builder_add_object(&b,
            SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
            SPA_PARAM_BUFFERS_buffers,  SPA_POD_CHOICE_RANGE_Int(8, 2, 32),
            SPA_PARAM_BUFFERS_blocks,   SPA_POD_Int(1),
            SPA_PARAM_BUFFERS_size,     SPA_POD_Int(g_frame_stride * g_frame_height),
            SPA_PARAM_BUFFERS_stride,   SPA_POD_Int(g_frame_stride),
            SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int((1 << SPA_DATA_MemPtr)));
        int r = pw_stream_update_params(g_pw_stream, params, n_params);
        fprintf(stderr, "cs-pw-helper: pw_stream_update_params returned %d\n", r);
    }

    /* We had INACTIVE set; now that the format is
     * negotiated, activate the stream. */
    if (g_pw_stream) {
        fprintf(stderr, "cs-pw-helper: calling pw_stream_set_active(TRUE)\n");
        pw_stream_set_active(g_pw_stream, true);
    }
}

static int g_process_call_count = 0;
static void on_stream_process(void *data) {
    (void)data;
    if (++g_process_call_count <= 3) {
        fprintf(stderr, "cs-pw-helper: on_stream_process called (count=%d) client_fd=%d\n",
            g_process_call_count, g_client_fd);
    }
    if (g_client_fd < 0) {
        struct pw_buffer *b;
        while ((b = pw_stream_dequeue_buffer(g_pw_stream)) != NULL) {
            pw_stream_queue_buffer(g_pw_stream, b);
        }
        return;
    }
    struct pw_buffer *buf;
    while ((buf = pw_stream_dequeue_buffer(g_pw_stream)) != NULL) {
        struct spa_buffer *sbuf = buf->buffer;
        struct spa_data *d = &sbuf->datas[0];
        if (d->data == NULL || d->chunk == NULL || d->chunk->size == 0) {
            pw_stream_queue_buffer(g_pw_stream, buf);
            continue;
        }
        if (write_all(g_client_fd, d->data, d->chunk->size) < 0) {
            fprintf(stderr, "cs-pw-helper: write to socket failed: %s\n", strerror(errno));
            pw_stream_queue_buffer(g_pw_stream, buf);
            g_interrupted = 1;
            return;
        }
        pw_stream_queue_buffer(g_pw_stream, buf);
    }
}

static const struct pw_stream_events stream_events = {
    .version = PW_VERSION_STREAM_EVENTS,
    .state_changed = on_stream_state_changed,
    .param_changed = on_stream_param_changed,
    .process = on_stream_process,
};

/* The pw_stream_connect call takes a pointer to an SPA POD
 * format descriptor. That pointer must remain valid until the
 * negotiation completes (i.e. until on_stream_param_changed fires
 * with a non-NULL param, or until the stream is destroyed). A
 * stack buffer is wrong - the function returns before the
 * negotiation happens. We use static storage. */
static uint8_t g_pod_buffer[1024];
static struct spa_pod *g_pod_params[1];

/* Build the format POD once at startup. We declare a reasonable
 * default (BGRA at the compositor's preferred size/framerate)
 * and let the source push its preferred format. */
static void build_format_pod(void) {
    struct spa_pod_builder b = {0};
    spa_pod_builder_init(&b, g_pod_buffer, sizeof(g_pod_buffer));
    /* Use SPA_PARAM_EnumFormat (not SPA_PARAM_Format) to enumerate
     * the formats we can accept. The server picks one and sends
     * us a Format param_changed event with the chosen format. */
    g_pod_params[0] = (struct spa_pod *)spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
        SPA_FORMAT_mediaType,       SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype,    SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format,    SPA_POD_CHOICE_ENUM_Id(
            8, SPA_VIDEO_FORMAT_BGRA, SPA_VIDEO_FORMAT_BGRx,
            SPA_VIDEO_FORMAT_BGR, SPA_VIDEO_FORMAT_RGBx,
            SPA_VIDEO_FORMAT_YUY2, SPA_VIDEO_FORMAT_YV12,
            SPA_VIDEO_FORMAT_I420, SPA_VIDEO_FORMAT_NV12),
        SPA_FORMAT_VIDEO_size,      SPA_POD_CHOICE_RANGE_Rectangle(
            &SPA_RECTANGLE(1920, 1080),
            &SPA_RECTANGLE(1, 1),
            &SPA_RECTANGLE(7680, 4320)),
        SPA_FORMAT_VIDEO_framerate, SPA_POD_CHOICE_RANGE_Fraction(
            &SPA_FRACTION(30, 1),
            &SPA_FRACTION(0, 1),
            &SPA_FRACTION(240, 1)));
}

/* setup_pw_stream must be called on the PipeWire thread. We
 * use pw_loop_signal_event to wake the loop with our own signal.
 * The signal handler runs in the PW thread context, so it can
 * safely call setup_pw_stream_on_pw_thread. */
static volatile int g_setup_pending = 0;
static uint32_t g_setup_node_id = 0;

static void setup_pw_stream_on_pw_thread(uint32_t node_id) {
    fprintf(stderr, "cs-pw-helper: setup_pw_stream_on_pw_thread node_id=%u\n", node_id);
    g_pw_stream = pw_stream_new(g_pw_core, "cs-pw-helper-capture", NULL);
    if (g_pw_stream == NULL) {
        fprintf(stderr, "cs-pw-helper: pw_stream_new failed\n");
        return;
    }
    pw_stream_add_listener(g_pw_stream, &g_stream_listener, &stream_events, NULL);
    /* Strategy: connect INACTIVE with our preferred BGRA
     * format. The source will respond with on_stream_param_changed
     * containing either:
     *   - The format we proposed (SPA_PARAM_Format) -> use it
     *   - A different format -> we may need to renegotiate
     * With INACTIVE, the stream pauses at PAUSED state until
     * we call pw_stream_set_active(TRUE). We do that after
     * we receive a Format param. */
    /* No INACTIVE flag: the source starts streaming
     * immediately with whatever it picks from our
     * EnumFormat. We can read the format from
     * on_stream_param_changed when it fires, and
     * also from the size of the first frame in
     * on_stream_process. */
    /* Use INACTIVE: stay at state 1 (PAUSED) until format
     * is negotiated, then call pw_stream_set_active(TRUE)
     * to go to state 2 (STREAMING). This gives us a chance
     * to call pw_stream_update_params with the buffer
     * config before any frames flow. */
    if (pw_stream_connect(g_pw_stream,
            SPA_DIRECTION_INPUT, node_id,
            PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_INACTIVE,
            (const struct spa_pod **)g_pod_params, 1) < 0) {
        fprintf(stderr, "cs-pw-helper: pw_stream_connect with EnumFormat failed; "
            "trying with NULL params\n");
        if (pw_stream_connect(g_pw_stream,
                SPA_DIRECTION_INPUT, node_id,
                PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_INACTIVE,
                NULL, 0) < 0) {
            fprintf(stderr, "cs-pw-helper: pw_stream_connect failed entirely\n");
            return;
        }
    }
    fprintf(stderr, "cs-pw-helper: pw_stream_connect succeeded; waiting for param negotiation\n");
}

static void on_signal_event(void *data, uint64_t count) {
    (void)data; (void)count;
    fprintf(stderr, "cs-pw-helper: on_signal_event pre-read: g_setup_node_id=%u setup_pending=%d\n",
        g_setup_node_id, g_setup_pending);
    if (g_setup_pending) {
        g_setup_pending = 0;
        fprintf(stderr, "cs-pw-helper: on_signal_event calling setup with node_id=%u\n", g_setup_node_id);
        setup_pw_stream_on_pw_thread(g_setup_node_id);
    }
}

static void schedule_pw_setup(uint32_t node_id) {
    g_setup_node_id = node_id;
    g_setup_pending = 1;
    /* Wake the PW loop. Our event handler (on_signal_event)
     * is called on the PW thread. */
    if (g_pw_event_source) {
        pw_loop_signal_event(g_pw_loop, g_pw_event_source);
    }
}

/* Called from the PW thread after pw_main_loop_run returns. */
static void pw_thread_check_setup(void) {
    if (g_setup_pending) {
        g_setup_pending = 0;
        setup_pw_stream_on_pw_thread(g_setup_node_id);
    }
}

static void on_core_done(void *data, uint32_t id, int seq) {
    (void)data; (void)id; (void)seq;
}

static void on_core_error(void *data, uint32_t id, int seq, int res, const char *message) {
    (void)data; (void)id; (void)seq;
    fprintf(stderr, "cs-pw-helper: core error: %s (%d)\n", message, res);
}

static const struct pw_core_events core_events = {
    .version = PW_VERSION_CORE_EVENTS,
    .done = on_core_done,
    .error = on_core_error,
};


/* Run pw_loop_iterate with a small budget. We call this
 * from a GLib timer source so PW events fire on the main
 * thread (the same thread that called the other PW APIs). */
static int pw_iterate_count = 0;
static gboolean pw_iterate(gpointer user_data) {
    (void)user_data;
    if (g_pw_loop == NULL) return FALSE;  /* stop */
    int r = pw_loop_iterate(g_pw_loop, -1);
    if (++pw_iterate_count <= 5 || (pw_iterate_count % 100) == 0) {
        fprintf(stderr, "cs-pw-helper: pw_iterate #%d returned %d\n", pw_iterate_count, r);
    }
    return TRUE;  /* keep ticking */
}

static gboolean on_probe_streams(gpointer user_data) {
    (void)user_data;
    fprintf(stderr, "cs-pw-helper: on_probe_streams fired\n");
    if (g_pw_node_id != 0) return FALSE;
    /* Enter the PW thread's context to call PW APIs. */
    pw_loop_enter(g_pw_loop);
    GVariant *streams = xdp_session_get_streams(g_session);
    if (streams == NULL) {
        fprintf(stderr, "cs-pw-helper: probe: xdp_session_get_streams returned NULL, retry in 1s\n");
        pw_loop_leave(g_pw_loop);
        return TRUE;
    }
    GVariantIter iter;
    if (!g_variant_iter_init(&iter, streams)) {
        fprintf(stderr, "cs-pw-helper: probe: g_variant_iter_init failed\n");
        pw_loop_leave(g_pw_loop);
        return FALSE;
    }
    GVariant *stream;
    if (g_variant_iter_next(&iter, "@(ua{sv})", &stream)) {
        g_variant_get(stream, "(u@a{sv})", &g_pw_node_id, NULL);
        fprintf(stderr, "cs-pw-helper: probe: using source node id %u\n", g_pw_node_id);
        g_variant_unref(stream);
    }
    if (g_pw_node_id == 0) {
        fprintf(stderr, "cs-pw-helper: probe: no streams available yet\n");
        pw_loop_leave(g_pw_loop);
        return TRUE;
    }
    /* We need to call setup_pw_stream (which uses PW APIs)
     * on the PW thread, not this one. schedule_pw_setup wakes
     * the PW thread by calling pw_main_loop_quit; the thread's
     * loop function then runs setup and re-enters pw_main_loop_run. */
    schedule_pw_setup(g_pw_node_id);
    pw_loop_leave(g_pw_loop);
    return FALSE;
}

static void *pw_thread_fn(void *arg) {
    (void)arg;
    fprintf(stderr, "cs-pw-helper: PipeWire main loop running on a dedicated thread\n");
    /* pw_main_loop_run blocks until something calls quit().
     * We start it once and let it process events forever. */
    pw_main_loop_run(g_pw_main_loop);
    fprintf(stderr, "cs-pw-helper: PipeWire main loop exited\n");
    return NULL;
}

static void on_session_start_finish(GObject *source, GAsyncResult *result, gpointer user_data) {
    (void)user_data;
    XdpSession *session = XDP_SESSION(source);
    GError *error = NULL;
    if (!xdp_session_start_finish(session, result, &error)) {
        fprintf(stderr, "cs-pw-helper: xdp_session_start failed: %s\n",
            error ? error->message : "?");
        g_clear_error(&error);
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_USER_CANCELLED);
    }
    fprintf(stderr, "cs-pw-helper: session started\n");
    int fd = xdp_session_open_pipewire_remote(session);
    if (fd < 0) {
        fprintf(stderr, "cs-pw-helper: xdp_session_open_pipewire_remote failed\n");
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_PIPEWIRE_FAILED);
    }
    fprintf(stderr, "cs-pw-helper: pipewire remote fd = %d\n", fd);
    pw_init(NULL, NULL);
    g_pw_main_loop = pw_main_loop_new(NULL);
    if (!g_pw_main_loop) {
        fprintf(stderr, "cs-pw-helper: pw_main_loop_new failed\n");
        close(fd);
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_PIPEWIRE_FAILED);
    }
    g_pw_loop = pw_main_loop_get_loop(g_pw_main_loop);
    g_pw_context = pw_context_new(g_pw_loop, NULL, 0);
    if (!g_pw_context) {
        fprintf(stderr, "cs-pw-helper: pw_context_new failed\n");
        close(fd);
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_PIPEWIRE_FAILED);
    }
    g_pw_core = pw_context_connect_fd(g_pw_context, fd, NULL, 0);
    if (!g_pw_core) {
        fprintf(stderr, "cs-pw-helper: pw_context_connect_fd failed\n");
        close(fd);
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_PIPEWIRE_FAILED);
    }
    pw_core_add_listener(g_pw_core, &g_remote_listener, &core_events, NULL);
    /* Add a signal event so we can wake the PW thread
     * from the GLib thread to run setup. The function
     * signature is (void *data, uint64_t count). */
    g_pw_event_source = pw_loop_add_event(g_pw_loop, on_signal_event, NULL);

    /* Probe the streams in 200ms (after the core handshake
     * completes). A timer is simpler than hooking pw_core_info
     * via the proxy. */
    g_timeout_add(200, (GSourceFunc)on_probe_streams, NULL);

    if (pthread_create(&g_pw_thread, NULL, pw_thread_fn, NULL) != 0) {
        fprintf(stderr, "cs-pw-helper: pthread_create failed\n");
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_PIPEWIRE_FAILED);
    }
    g_pw_thread_started = 1;
}

static void on_create_session_finish(GObject *source, GAsyncResult *result, gpointer user_data) {
    (void)user_data;
    XdpPortal *portal = XDP_PORTAL(source);
    GError *error = NULL;
    g_session = xdp_portal_create_screencast_session_finish(portal, result, &error);
    if (g_session == NULL) {
        fprintf(stderr, "cs-pw-helper: xdp_portal_create_screencast_session_finish failed: %s\n",
            error ? error->message : "?");
        g_clear_error(&error);
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_PORTAL_FAILED);
    }
    fprintf(stderr, "cs-pw-helper: session created; starting (waiting for user)...\n");
    xdp_session_start(g_session, NULL, NULL, on_session_start_finish, NULL);
}

static void usage(const char *argv0) {
    fprintf(stderr,
        "Usage: %s [OPTIONS]\n"
        "\n"
        "  --socket PATH     Unix socket path to write frames to (required)\n"
        "  --source-type N   1=monitor 2=window 4=virtual (default: 1)\n"
        "  --framerate N     Desired framerate (default: 30)\n"
        "  --cursor          Include the mouse cursor in the capture\n"
        "  --node-id N       Test mode: skip the portal and connect to PipeWire node N.\n"
        "                    Use pw-cli to find a node id, e.g. a webcam.\n"
        "  -h, --help        Show this help\n",
        argv0);
}

int main(int argc, char *argv[]) {
    int source_type = SOURCE_TYPE_MONITOR;
    int framerate = 30;
    int include_cursor = 0;

    static struct option long_opts[] = {
        {"socket",      required_argument, 0, 's'},
        {"source-type", required_argument, 0, 't'},
        {"framerate",   required_argument, 0, 'f'},
        {"cursor",      no_argument,       0, 'c'},
        {"node-id",     required_argument, 0, 'N'},
        {"help",        no_argument,       0, 'h'},
        {0, 0, 0, 0}
    };
    long node_id_override = -1;
    int opt;
    while ((opt = getopt_long(argc, argv, "s:t:f:cN:h", long_opts, NULL)) != -1) {
        switch (opt) {
            case 's': g_socket_path = optarg; break;
            case 't': source_type = atoi(optarg); break;
            case 'f': framerate = atoi(optarg); break;
            case 'c': include_cursor = 1; break;
            case 'N': node_id_override = atol(optarg); break;
            case 'h': usage(argv[0]); return EXIT_OK;
            default:  usage(argv[0]); return EXIT_BAD_ARGS;
        }
    }
    if (!g_socket_path) { usage(argv[0]); return EXIT_BAD_ARGS; }
    if (source_type != SOURCE_TYPE_MONITOR &&
        source_type != SOURCE_TYPE_WINDOW &&
        source_type != SOURCE_TYPE_VIRTUAL) {
        fprintf(stderr, "cs-pw-helper: invalid --source-type %d\n", source_type);
        return EXIT_BAD_ARGS;
    }
    g_framerate_num = framerate;
    g_framerate_den = 1;

    signal(SIGINT, on_sigint);
    signal(SIGTERM, on_sigint);
    signal(SIGPIPE, SIG_IGN);

    g_listen_fd = create_unix_socket(g_socket_path);
    if (g_listen_fd < 0) return EXIT_PIPEWIRE_FAILED;
    GIOChannel *io = g_io_channel_unix_new(g_listen_fd);
    g_io_channel_set_flags(io, G_IO_FLAG_NONBLOCK, NULL);
    g_io_add_watch(io, G_IO_IN, on_accept_client, NULL);
    g_io_channel_unref(io);
    fprintf(stderr, "cs-pw-helper: listening on %s\n", g_socket_path);

    build_format_pod();

    g_portal = xdp_portal_new();
    if (!g_portal) {
        fprintf(stderr, "cs-pw-helper: xdp_portal_new failed\n");
        return EXIT_PORTAL_FAILED;
    }

    /* Test mode: --node-id N skips the portal and connects
     * to a PipeWire node directly. Useful for iterating on
     * the format-negotiation path without needing the user
     * to click Share in a portal dialog every time. */
    if (node_id_override >= 0) {
        fprintf(stderr, "cs-pw-helper: TEST MODE: skipping portal, connecting to node %ld\n",
            node_id_override);
        /* Set up a fake session so xdp_session_get_streams
         * works. We don't actually need a session since we
         * have the node id, but the code paths that read
         * streams from the session expect it to exist. */
        g_session = NULL;
        /* Initialize PW directly. */
        pw_init(NULL, NULL);
        g_pw_main_loop = pw_main_loop_new(NULL);
        g_pw_loop = pw_main_loop_get_loop(g_pw_main_loop);
        g_pw_context = pw_context_new(g_pw_loop, NULL, 0);
        if (!g_pw_context) {
            fprintf(stderr, "cs-pw-helper: pw_context_new failed\n");
            return EXIT_PIPEWIRE_FAILED;
        }
        g_pw_core = pw_context_connect(g_pw_context, NULL, 0);
        if (!g_pw_core) {
            fprintf(stderr, "cs-pw-helper: pw_context_connect failed\n");
            return EXIT_PIPEWIRE_FAILED;
        }
        pw_core_add_listener(g_pw_core, &g_remote_listener, &core_events, NULL);
        build_format_pod();
        if (pthread_create(&g_pw_thread, NULL, pw_thread_fn, NULL) != 0) {
            fprintf(stderr, "cs-pw-helper: pthread_create failed\n");
            return EXIT_PIPEWIRE_FAILED;
        }
        g_pw_thread_started = 1;
        /* Schedule setup on the PW thread. We use
         * __sync_synchronize() to make sure the writes
         * to g_setup_node_id and g_setup_pending are
         * visible to the PW thread before the signal
         * wakes it. */
        g_setup_node_id = (uint32_t)node_id_override;
        __sync_synchronize();
        g_setup_pending = 1;
        __sync_synchronize();
        fprintf(stderr, "cs-pw-helper: test mode pre-signal: g_setup_node_id=%u g_setup_pending=%d\n",
            g_setup_node_id, g_setup_pending);
        if (g_pw_event_source == NULL) {
            g_pw_event_source = pw_loop_add_event(g_pw_loop, on_signal_event, NULL);
        }
        if (g_pw_event_source) pw_loop_signal_event(g_pw_loop, g_pw_event_source);
        fprintf(stderr, "cs-pw-helper: test mode post-signal\n");
        g_main_loop = g_main_loop_new(NULL, FALSE);
    } else {
        fprintf(stderr, "cs-pw-helper: requesting screen capture (this opens a system dialog)...\n");
        xdp_portal_create_screencast_session(
            g_portal,
            source_type,
            XDP_SCREENCAST_FLAG_NONE,
            include_cursor ? XDP_CURSOR_MODE_EMBEDDED : XDP_CURSOR_MODE_HIDDEN,
            XDP_PERSIST_MODE_NONE,
            NULL, NULL,
            on_create_session_finish, NULL);

        g_main_loop = g_main_loop_new(NULL, FALSE);
    }
    fprintf(stderr, "cs-pw-helper: entering main loop\n");
    g_main_loop_run(g_main_loop);
    fprintf(stderr, "cs-pw-helper: main loop exited\n");

    if (g_pw_thread_started) pthread_join(g_pw_thread, NULL);
    if (g_pw_stream) pw_stream_destroy(g_pw_stream);
    if (g_pw_core) pw_core_disconnect(g_pw_core);
    if (g_pw_context) pw_context_destroy(g_pw_context);
    if (g_pw_main_loop) pw_main_loop_destroy(g_pw_main_loop);
    pw_deinit();

    if (g_session) g_object_unref(g_session);
    if (g_portal) g_object_unref(g_portal);
    if (g_client_fd >= 0) close(g_client_fd);
    if (g_listen_fd >= 0) close(g_listen_fd);
    unlink(g_socket_path);

    return EXIT_OK;
}
