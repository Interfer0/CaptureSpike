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
#include <sys/mman.h>
#include <sys/stat.h>

#include <glib.h>
#include <gtk/gtk.h>
#include <gio/gio.h>
#include <libportal/portal.h>
#include <libportal/portal-enums.h>

#include <pipewire/pipewire.h>
#include <pipewire/stream.h>
#include <spa/param/video/format-utils.h>
#include <spa/param/latency.h>
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
#define EXIT_MULTI_SOURCE    6
/* The variant walk can only recover from a buffer-shape error (the
 * source rejected our pod). A resource error means the source or the
 * PipeWire connection is unhappy for a different reason - the same
 * pod tried against the same node will keep failing. Bail out. */
#define EXIT_RESOURCE_ERROR  7

/* One Window source records one window. The portal returns one PipeWire
 * node per selected source, and we drive a single stream into a single
 * socket for a single ffmpeg, so more than one has to be re-asked.
 *
 * We already request a single source: libportal sends "multiple: false"
 * whenever XDP_SCREENCAST_FLAG_MULTIPLE is not set, and that is the only
 * option in the SelectSources options dict that constrains the count.
 * GNOME's picker ignores it, so the request has to be enforced here by
 * explaining and asking again. */
#define MAX_SOURCE_RETRIES 3

/* Globals so the session can be restarted after a bad selection. */
static int   source_type = SOURCE_TYPE_MONITOR;
static int   include_cursor = 0;
static int   g_source_retries = 0;

static volatile sig_atomic_t g_interrupted = 0;
static volatile sig_atomic_t g_exit_code = EXIT_OK;
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
/* The --framerate the caller asked for. A screencast source reports a
 * variable framerate, so this is what we fall back to. */
static int g_requested_fps = 30;
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

/* Protocol format codes, matching PwStreamProtocol.PixelFormat on the
 * .NET side. FMT is reported so the consumer labels the stream
 * correctly: Screen capture negotiates BGRx and Window capture BGRA,
 * and they were both being reported as BGRA. */
static unsigned protocol_format_code(void) {
    switch (g_frame_format) {
        case SPA_VIDEO_FORMAT_BGRx:
        case SPA_VIDEO_FORMAT_RGB:   return 3; /* Bgrx: undefined 4th byte */
        case SPA_VIDEO_FORMAT_BGRA:
        case SPA_VIDEO_FORMAT_RGBA:  return 1; /* Bgra */
        case SPA_VIDEO_FORMAT_BGR:   return 2; /* Bgr0 */
        default:                     return 1;
    }
}

static void emit_header_line(int width, int height, int fps_num, int fps_den) {
    double fps = fps_den > 0 ? (double)fps_num / (double)fps_den : 0.0;
    fprintf(stderr, "CSWH W=%d H=%d FPS=%.2f FMT=%u\n",
            width, height, fps, protocol_format_code());
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

static int retry_next_variant(void *data);
static void retry_next_variant_idle(void *data);

/* True if the pipewire error string indicates a resource or connection
 * problem rather than a buffer-shape rejection. The variant walk is for
 * the latter; for the former, the next variant will hit the same error
 * on the same node. Bail out, do not walk. */
static int is_resource_error(const char *err) {
    if (err == NULL) return 0;
    /* errno names that pipewire strings to its errors (e.g. "16
     * (Device or resource busy)"). Match by the human-readable tail
     * because the numeric part is preceded by a sign and variable
     * padding. */
    return strstr(err, "Device or resource busy") != NULL  /* EBUSY  */
        || strstr(err, "Bad file descriptor") != NULL      /* EBADF  */
        || strstr(err, "Resource temporarily unavailable") != NULL /* EAGAIN */
        || strstr(err, "Connection refused") != NULL        /* ECONNREFUSED */
        || strstr(err, "Connection reset") != NULL           /* ECONNRESET */
        || strstr(err, "Broken pipe") != NULL;              /* EPIPE  */
}

static void bail_resource(const char *err) {
    fprintf(stderr, "cs-pw-helper: stream failed with a non-negotiation error "
            "(\"%s\"); the variant walk is for buffer-shape errors and will not "
            "recover from a resource problem. Giving up.\n", err ? err : "no reason given");
    g_exit_code = EXIT_RESOURCE_ERROR;
    g_interrupted = 1;
    if (g_main_loop) g_main_loop_quit(g_main_loop);
}
static void setup_pw_stream_on_pw_thread(uint32_t node_id);

/* ---- Buffer negotiation variants -------------------------------------
 *
 * The portal hands us a fd to a PipeWire daemon that hosts the screencast
 * source in another process. There is no session manager on that remote to
 * negotiate buffers for us, so we have to answer with our own
 * SPA_PARAM_Buffers pod and hope the source fixates it. Which pod shape a
 * given compositor source accepts is not something the spec pins down, so
 * instead of guessing one shape and paying a portal "Share" click per
 * attempt, we walk a table of candidates: on every stream error we destroy
 * the stream, advance to the next candidate and reconnect. One Share click
 * therefore reports which shape actually works.
 *
 * Why the previous single shape failed, from the pipewire daemon log:
 *
 *   source offers : buffers 16/2/16, blocks 1, align 16, dataType MemFd|DmaBuf
 *   we requested  : buffers 8/2/8, blocks 1, size 8294400, stride 7680,
 *                   dataType MemPtr|DmaBuf
 *
 * The source fixated nothing against our pod ("output param 0"), so
 * pw_buffers_negotiate()'s param_filter() fell through to
 * "return -EINVAL" -> "error alloc buffers: Invalid argument". Two
 * suspects: the explicit size/stride the source will not match, and
 * MemPtr, which a cross-process source can never hand out because the
 * memory would live in our address space, not ours to read. The variants
 * below vary those two independently so the culprit is unambiguous.
 */

enum neg_flags {
    NEG_WANT_SIZE     = 1 << 0,  /* include SPA_PARAM_BUFFERS_size */
    NEG_WANT_STRIDE   = 1 << 1,  /* include SPA_PARAM_BUFFERS_stride */
    NEG_WANT_ALIGN    = 1 << 2,  /* include SPA_PARAM_BUFFERS_align */
    NEG_WANT_LATENCY  = 1 << 3,  /* also send SPA_PARAM_Latency */
    NEG_WANT_METAS    = 1 << 4,  /* send Header + VideoCrop metas */
    NEG_NO_BUFFERS    = 1 << 6,  /* send no Buffers pod at all */
};

struct neg_variant {
    const char *name;
    uint32_t flags;
    uint32_t n_buffers;   /* requested buffer count (min 2) */
    uint32_t data_types;  /* mask of 1u << SPA_DATA_* */
};

static const struct neg_variant g_variants[] = {
    /* Exactly what the source advertises. Port 70 of the gnome-shell
     * screencast node offers:
     *   buffers 16/2/16, blocks 1, align 16, dataType Int 4
     * and spa_data_type is MemPtr=1, MemFd=2, DmaBuf=3, so Int 4 is
     * 1<<SPA_DATA_MemFd - MemFd and nothing else. Asking for MemPtr or
     * DmaBuf therefore has an empty intersection with the only type the
     * source offers, the source fixates nothing, and pw_buffers_negotiate()
     * bails out with -EINVAL. So: MemFd, its buffer count, its alignment. */
    { "memfd mirror",
      NEG_WANT_METAS | NEG_WANT_ALIGN, 16, 1u << SPA_DATA_MemFd },
    /* Same, plus the size/stride we derive from the negotiated format. */
    { "memfd +size+stride",
      NEG_WANT_METAS | NEG_WANT_ALIGN | NEG_WANT_SIZE | NEG_WANT_STRIDE,
      16, 1u << SPA_DATA_MemFd },
    /* MemFd together with MemPtr: harmless if the source ignores it, and
     * MAP_BUFFERS-style consumers may prefer it. */
    { "memfd|memptr",
      NEG_WANT_METAS | NEG_WANT_ALIGN, 16,
      (1u << SPA_DATA_MemFd) | (1u << SPA_DATA_MemPtr) },
    /* Every type, in case a compositor is more permissive. */
    { "all-types",
      NEG_WANT_METAS | NEG_WANT_ALIGN, 16,
      (1u << SPA_DATA_MemFd) | (1u << SPA_DATA_MemPtr) | (1u << SPA_DATA_DmaBuf) },
    /* MemFd without the metas, to check whether a rejected meta is the
     * problem. The source offers Busy, VideoCrop, Cursor, Header and
     * VideoDamage; if one of those is mandatory we would see it here. */
    { "memfd bare", 0, 16, 1u << SPA_DATA_MemFd },
    /* MemFd with our own buffer count instead of the source's. */
    { "memfd buffers=8", NEG_WANT_METAS, 8, 1u << SPA_DATA_MemFd },
    /* DmaBuf, for compositors that hand out dmabufs instead. */
    { "dmabuf-only", NEG_WANT_METAS | NEG_WANT_ALIGN, 8, 1u << SPA_DATA_DmaBuf },
    /* MemFd plus an explicit latency. */
    { "memfd +latency",
      NEG_WANT_METAS | NEG_WANT_ALIGN | NEG_WANT_LATENCY, 16, 1u << SPA_DATA_MemFd },
    /* MemPtr, for a source running in our own process. */
    { "memptr-only", NEG_WANT_METAS | NEG_WANT_ALIGN, 8, 1u << SPA_DATA_MemPtr },
    /* Hand the daemon nothing and let its defaults apply. */
    { "no buffers pod", NEG_NO_BUFFERS, 0, 0 },
};

#define N_VARIANTS ((int)(sizeof(g_variants) / sizeof(g_variants[0])))

static int g_variant = -1;        /* -1 = no attempt made yet */
static int g_have_format = 0;
static int g_header_emitted = 0;
static uint32_t g_active_node = 0;

/* Send the SPA_PARAM_Buffers / Meta / Latency pod for the current variant.
 *
 * IMPORTANT: the pods are built in STATIC storage. PipeWire keeps the pod
 * pointers and dereferences them after this function returns, so stack
 * storage would be a use-after-free. pw_stream_update_params() copies the
 * pod immediately, but we keep it static anyway to stay safe if that
 * changes. */
static void apply_negotiation_variant(void) {
    static uint8_t pod_storage[2048];
    static struct spa_pod *params[8];
    const struct neg_variant *v;
    struct spa_pod_builder b = {0};
    int n = 0, r;
    uint64_t latency_ns;

    if (g_variant < 0 || g_variant >= N_VARIANTS) return;
    v = &g_variants[g_variant];

    spa_pod_builder_init(&b, pod_storage, sizeof(pod_storage));

    if (!SPA_FLAG_IS_SET(v->flags, NEG_NO_BUFFERS)) {
        /* push_object/pop rather than add_object: the properties of this
         * object are conditional, and spa_pod_builder_add_object() closes
         * its frame, so anything appended after it lands outside the object
         * and produces a malformed pod. */
        struct spa_pod_frame f;
        spa_pod_builder_push_object(&b, &f,
            SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers);
        spa_pod_builder_add(&b,
            SPA_PARAM_BUFFERS_buffers,
            SPA_POD_CHOICE_RANGE_Int(v->n_buffers, 2, v->n_buffers), 0);
        spa_pod_builder_add(&b, SPA_PARAM_BUFFERS_blocks, SPA_POD_Int(1), 0);
        if (SPA_FLAG_IS_SET(v->flags, NEG_WANT_SIZE))
            spa_pod_builder_add(&b, SPA_PARAM_BUFFERS_size,
                SPA_POD_Int((uint32_t)g_frame_stride * (uint32_t)g_frame_height), 0);
        if (SPA_FLAG_IS_SET(v->flags, NEG_WANT_STRIDE))
            spa_pod_builder_add(&b, SPA_PARAM_BUFFERS_stride,
                SPA_POD_Int(g_frame_stride), 0);
        if (SPA_FLAG_IS_SET(v->flags, NEG_WANT_ALIGN))
            spa_pod_builder_add(&b, SPA_PARAM_BUFFERS_align, SPA_POD_Int(16), 0);
        spa_pod_builder_add(&b, SPA_PARAM_BUFFERS_dataType,
            SPA_POD_CHOICE_FLAGS_Int(v->data_types), 0);
        params[n++] = spa_pod_builder_pop(&b, &f);

        if (SPA_FLAG_IS_SET(v->flags, NEG_WANT_METAS)) {
            params[n++] = spa_pod_builder_add_object(&b,
                SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
                SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Header),
                SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_header)));
            params[n++] = spa_pod_builder_add_object(&b,
                SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
                SPA_PARAM_META_type, SPA_POD_Id(SPA_META_VideoCrop),
                SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_region)));
        }
    }

    if (SPA_FLAG_IS_SET(v->flags, NEG_WANT_LATENCY)) {
        if (g_framerate_num > 0 && g_framerate_den > 0)
            latency_ns = (1000000000ULL * (uint64_t)g_framerate_den) / (uint64_t)g_framerate_num;
        else
            latency_ns = 1000000000ULL / 30;
        /* The direction must match the stream's or stream.c's add_param()
         * drops the pod as a locked param of the wrong direction. */
        params[n++] = spa_pod_builder_add_object(&b,
            SPA_TYPE_OBJECT_ParamLatency, SPA_PARAM_Latency,
            SPA_PARAM_LATENCY_direction, SPA_POD_Id(SPA_DIRECTION_INPUT),
            SPA_PARAM_LATENCY_minNs, SPA_POD_Long(latency_ns),
            SPA_PARAM_LATENCY_maxNs, SPA_POD_Long(latency_ns));
    }

    r = pw_stream_update_params(g_pw_stream, (const struct spa_pod **)params, n);
    fprintf(stderr, "cs-pw-helper: variant %d (%s): update_params -> %d [%d pods, "
            "buffers=%u types=0x%x%s]\n",
            g_variant, v->name, r, n, v->n_buffers, v->data_types,
            g_have_format ? ", format known" : ", no format yet");
}

/* Destroy the current stream, move to the next negotiation variant and
 * reconnect to the same source node. Runs on the PipeWire thread via
 * pw_loop_invoke(), i.e. after the failing state_changed callback has
 * returned, so destroying the stream here is safe. */
static void retry_next_variant_idle(void *data) {
    (void)data;
    retry_next_variant(NULL);
}

static int retry_next_variant(void *data) {
    (void)data;
    if (g_pw_stream) {
        pw_stream_destroy(g_pw_stream);
        g_pw_stream = NULL;
    }
    g_have_format = 0;
    g_variant++;
    if (g_variant >= N_VARIANTS) {
        fprintf(stderr, "cs-pw-helper: all %d negotiation variants failed\n", N_VARIANTS);
        g_interrupted = 1;
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        return 0;
    }
    fprintf(stderr, "cs-pw-helper: === retrying with variant %d (%s) ===\n",
            g_variant, g_variants[g_variant].name);
    setup_pw_stream_on_pw_thread(g_active_node);
    return 0;
}

static void on_stream_state_changed(void *data,
        enum pw_stream_state old, enum pw_stream_state new, const char *err) {
    (void)data; (void)old;
    fprintf(stderr, "cs-pw-helper: stream state %d -> %d (%s)\n", old, new, err ? err : "ok");

    if (new == PW_STREAM_STATE_ERROR || new == PW_STREAM_STATE_UNCONNECTED) {
        if (is_resource_error(err)) {
            bail_resource(err);
            return;
        }
        fprintf(stderr, "cs-pw-helper: stream failed (%s); advancing to the next "
                "negotiation variant\n", err ? err : "no reason given");
        /* Deferred: we must not destroy the stream from inside its own
         * state_changed callback. */
        pw_loop_add_idle(g_pw_loop, true, retry_next_variant_idle, NULL);
        return;
    }

    /* We connect with PW_STREAM_FLAG_INACTIVE, so the stream parks at
     * PAUSED and nothing has been negotiated yet. Answer with the buffer
     * pod BEFORE activating: activating is what starts the link's buffer
     * negotiation, and a negotiation started against an empty buffer set is
     * what produced "error alloc buffers: Invalid argument" before.
     *
     * Most variants deliberately omit size/stride so they are already
     * complete here, before any format is known. The two that want an
     * explicit size are re-sent by on_stream_param_changed() once the
     * format is known, which triggers a renegotiation. */
    if (new == PW_STREAM_STATE_PAUSED && g_pw_stream != NULL) {
        apply_negotiation_variant();
        pw_stream_set_active(g_pw_stream, true);
    }

    if (new == PW_STREAM_STATE_STREAMING) {
        fprintf(stderr, "cs-pw-helper: *** STREAMING with variant %d (%s) ***\n",
                g_variant, (g_variant >= 0 && g_variant < N_VARIANTS)
                    ? g_variants[g_variant].name : "?");
        g_interrupted = 0;
    }
}

static void on_stream_param_changed(void *data, uint32_t id, const struct spa_pod *param) {
    (void)data;
    /* Only SPA_PARAM_Format matters here. The stream also emits Props (2)
     * and Latency (15) events, and answering those with a buffers pod is
     * what left several stale buffer variants on the node last time. */
    if (id != SPA_PARAM_Format) return;
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
    /* Screencast sources are driven by compositor repaints, not a clock,
     * so they report the framerate as 0/1 ("as fast as frames arrive").
     * The .NET side needs a real rate to build ffmpeg's -framerate, and it
     * rejects a header whose FPS is not greater than zero, so fall back to
     * the rate the caller asked for on the command line. */
    if (g_framerate_num <= 0 || g_framerate_den <= 0) {
        fprintf(stderr, "cs-pw-helper: source reports a variable framerate "
                "(%d/%d); using the requested %d fps\n",
                fmt.framerate.num, fmt.framerate.denom, g_requested_fps);
        g_framerate_num = g_requested_fps;
        g_framerate_den = 1;
    }
    switch (fmt.format) {
        case SPA_VIDEO_FORMAT_BGRA: g_frame_format = SPA_VIDEO_FORMAT_BGRA; break;
        case SPA_VIDEO_FORMAT_BGRx:
        case SPA_VIDEO_FORMAT_BGR: g_frame_format = SPA_VIDEO_FORMAT_BGRx; break;
        default:
            g_frame_format = fmt.format;
            break;
    }
    /* Compute per-format stride. BGRA/BGRx/RGBx/RGBA = 4 bytes/pixel,
     * YUY2 = 2. The width*4 default is only right for the BGRA family. */
    int bytes_per_pixel;
    switch (g_frame_format) {
        case SPA_VIDEO_FORMAT_BGRA:
        case SPA_VIDEO_FORMAT_BGRx:
        case SPA_VIDEO_FORMAT_RGBA:
        case SPA_VIDEO_FORMAT_RGBx:
        case SPA_VIDEO_FORMAT_BGR:
        case SPA_VIDEO_FORMAT_RGB:
            bytes_per_pixel = 4; break;
        case SPA_VIDEO_FORMAT_YUY2:
        case SPA_VIDEO_FORMAT_YVYU:
        case SPA_VIDEO_FORMAT_UYVY:
        case SPA_VIDEO_FORMAT_VYUY:
            bytes_per_pixel = 2; break;
        default:
            bytes_per_pixel = 4;
    }
    g_frame_stride = g_frame_width * bytes_per_pixel;
    fprintf(stderr, "cs-pw-helper: video %dx%d stride=%d @ %d/%d fps, format %d\n",
        g_frame_width, g_frame_height, g_frame_stride,
        g_framerate_num, g_framerate_den, g_frame_format);
    if (!g_header_emitted) {
        g_header_emitted = 1;
        emit_header_line(g_frame_width, g_frame_height, g_framerate_num, g_framerate_den);
    }
    g_have_format = 1;
    apply_negotiation_variant();
}

static int g_process_call_count = 0;
/* Resolve a spa_data to a CPU pointer.
 *
 * The screencast source hands us SPA_DATA_MemFd buffers, so d->data is
 * NULL and the pixels live in a shared memfd that we have to map
 * ourselves. All buffers of a stream share one fd and differ only by
 * mapoffset, so we map the whole fd once and index into it. */
static void *g_map_fd_cache = NULL;
static void *g_map_base = NULL;
static size_t g_map_len = 0;

static void *map_buffer_data(struct spa_data *d) {
    if (d->data != NULL) return d->data;
    if (d->fd < 0) return NULL;

    if (g_map_fd_cache != (void *)(intptr_t)d->fd) {
        struct stat st;
        off_t size;
        void *base;
        if (g_map_base != NULL) { munmap(g_map_base, g_map_len); g_map_base = NULL; g_map_len = 0; }
        if (fstat(d->fd, &st) < 0 || st.st_size <= 0) return NULL;
        size = st.st_size;
        base = mmap(NULL, (size_t)size, PROT_READ, MAP_SHARED, d->fd, 0);
        if (base == MAP_FAILED) {
            fprintf(stderr, "cs-pw-helper: mmap(fd=%d, %lld) failed: %s\n",
                    (int)d->fd, (long long)size, strerror(errno));
            g_map_fd_cache = NULL;
            return NULL;
        }
        g_map_base = base;
        g_map_len = (size_t)size;
        g_map_fd_cache = (void *)(intptr_t)d->fd;
        fprintf(stderr, "cs-pw-helper: mapped shared buffer fd=%d size=%lld\n",
                (int)d->fd, (long long)size);
    }
    if ((size_t)d->mapoffset >= g_map_len) return NULL;
    return (char *)g_map_base + d->mapoffset;
}

static void on_stream_process(void *data) {
    (void)data;
    if (++g_process_call_count <= 3) {
        fprintf(stderr, "cs-pw-helper: on_stream_process called (count=%d) client_fd=%d\n",
            g_process_call_count, g_client_fd);
    }
    /* If we never received a Format event, derive the
     * size from the first frame. We assume BGRA (4
     * bytes/pixel) which is the screencast default. */
    if (g_frame_width == 0) {
        struct pw_buffer *b = pw_stream_dequeue_buffer(g_pw_stream);
        if (b) {
            struct spa_buffer *sbuf = b->buffer;
            if (sbuf->datas[0].chunk && sbuf->datas[0].chunk->size > 0) {
                uint32_t stride = sbuf->datas[0].chunk->stride;
                if (stride == 0) stride = 1920 * 4;  /* assume full HD BGRA */
                g_frame_width = stride / 4;
                g_frame_height = sbuf->datas[0].chunk->size / stride;
                g_frame_format = SPA_VIDEO_FORMAT_BGRA;
                g_frame_stride = stride;
                fprintf(stderr, "cs-pw-helper: derived format from first frame: %dx%d stride=%d\n",
                    g_frame_width, g_frame_height, g_frame_stride);
                /* Mark the header as emitted so the Format event, if
                 * it arrives later, does not write a second CSWH line.
                 * The first-frame fallback may be wrong about the
                 * pixel format (we assume BGRA when in fact the
                 * source might be BGRx), but the .NET parser takes
                 * the first CSWH it sees and ignores the rest, so
                 * a single correct line is what we want. */
                g_header_emitted = 1;
                emit_header_line(g_frame_width, g_frame_height, g_framerate_num, g_framerate_den);
            }
            pw_stream_queue_buffer(g_pw_stream, b);
        }
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
        void *pixels;
        uint32_t offset;
        if (d->chunk == NULL || d->chunk->size == 0) {
            if (++g_process_call_count <= 8) {
                fprintf(stderr, "cs-pw-helper: process: chunk missing or size=0\n");
            }
            pw_stream_queue_buffer(g_pw_stream, buf);
            continue;
        }
        pixels = map_buffer_data(d);
        if (pixels == NULL) {
            if (++g_process_call_count <= 8) {
                fprintf(stderr, "cs-pw-helper: process: no CPU access (type=%u fd=%d)\n",
                        (unsigned)d->type, (int)d->fd);
            }
            pw_stream_queue_buffer(g_pw_stream, buf);
            continue;
        }
        /* The frame starts at chunk->offset inside the block. For a full
         * screencast frame that offset is 0 and the block is contiguous,
         * which is what ffmpeg's rawvideo demuxer expects. */
        offset = d->chunk->offset;
        if (++g_process_call_count <= 8) {
            fprintf(stderr, "cs-pw-helper: process: writing %u bytes at offset %u "
                    "to client_fd=%d\n", d->chunk->size, offset, g_client_fd);
        }
        if (write_all(g_client_fd, (char *)pixels + offset, d->chunk->size) < 0) {
            fprintf(stderr, "cs-pw-helper: write to socket failed: %s\n", strerror(errno));
            /* The ffmpeg consumer is gone: output closed, disk full,
             * or the helper is being torn down. Without this the
             * PipeWire data loop keeps calling on_stream_process,
             * which fails in the same way, until the process is killed
             * with SIGINT - the previous fix made other paths quit
             * the main loop, this one slipped through. Quit the loop
             * with a non-zero exit code so the .NET side reports this
             * rather than treating it as a clean exit. Don't requeue
             * the buffer: it would just sit in the pool. */
            g_exit_code = EXIT_STREAM_ERROR;
            g_interrupted = 1;
            if (g_main_loop) g_main_loop_quit(g_main_loop);
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
            /* BGRA/BGRx/RGBx/RGBA are preferred for screencast
             * (gnome-remote-desktop, wlroots-portal). YUY2 is a
             * fallback for V4L2 cameras in --node-id test mode.
             * Order matters: the first one is the default. */
            SPA_FORMAT_VIDEO_format,    SPA_POD_CHOICE_ENUM_Id(
                6, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRA,
                SPA_VIDEO_FORMAT_RGBx, SPA_VIDEO_FORMAT_RGBA,
                SPA_VIDEO_FORMAT_YUY2, SPA_VIDEO_FORMAT_I420),
            SPA_FORMAT_VIDEO_size,      SPA_POD_CHOICE_RANGE_Rectangle(
                &SPA_RECTANGLE(0, 0),
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
    g_active_node = node_id;
    if (g_variant < 0) g_variant = 0;
    fprintf(stderr, "cs-pw-helper: negotiation variant %d (%s)\n",
            g_variant, g_variants[g_variant].name);
    /* MediaType=Video, Category=Capture, Role=Screen.
     * The role tells PipeWire this is a screencast capture,
     * not a V4L2 camera capture. The screencast source
     * (gnome-remote-desktop / wlroots-portal) will only
     * autoconnect to streams with the right role. */
    struct pw_properties *props = pw_properties_new(
        PW_KEY_MEDIA_TYPE, "Video",
        PW_KEY_MEDIA_CATEGORY, "Capture",
        PW_KEY_MEDIA_ROLE, "Screen",
        NULL);
    g_pw_stream = pw_stream_new(g_pw_core, "cs-pw-helper-capture", props);
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

    /* Proactively query the source's current format. Some
     * sources (V4L2/webcam) never push an on_stream_param_changed
     * for Format, so the negotiation would deadlock. We get the
     * format up front; the response is delivered via
     * on_stream_param_changed as usual. */
    {
        uint8_t buf[1024];
        struct spa_pod_builder b = {0};
        spa_pod_builder_init(&b, buf, sizeof(buf));
        const struct spa_pod *params[1];
        params[0] = spa_pod_builder_add_object(&b,
            SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
            SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
            SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw));
        int r = pw_stream_update_params(g_pw_stream, params, 1);
        fprintf(stderr, "cs-pw-helper: query EnumFormat via update_params returned %d\n", r);
    }
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
    /* Test mode has __sync_synchronize() between the g_setup_node_id
     * and g_setup_pending writes and again before the implicit signal;
     * the portal path used to skip them. The PW thread reads
     * g_setup_node_id and g_setup_pending separately, so without a
     * release barrier it can observe g_setup_pending == 1 with
     * g_setup_node_id still at its default. C11 race; works on x86 by
     * accident, races on weakly-ordered architectures. */
    __sync_synchronize();
    g_setup_pending = 1;
    __sync_synchronize();
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
    /* xdp_session_get_streams is a D-Bus call (handled by
     * GLib), not a PipeWire API. It does NOT need the PW
     * thread's context. The old code wrapped it in
     * pw_loop_enter/leave which is what was producing the
     * "pthread_equal(impl->thread, thread_id) failed" warnings
     * and, more importantly, was forcing the negotiation onto
     * the wrong thread, which is why on_stream_param_changed
     * (Format) never fired. */
    GVariant *streams = xdp_session_get_streams(g_session);
    if (streams == NULL) {
        fprintf(stderr, "cs-pw-helper: probe: xdp_session_get_streams returned NULL, retry in 1s\n");
        return TRUE;
    }
    GVariantIter iter;
    if (!g_variant_iter_init(&iter, streams)) {
        fprintf(stderr, "cs-pw-helper: probe: g_variant_iter_init failed\n");
        return FALSE;
    }
    GVariant *stream;
    /* Count the streams. The portal returns one PipeWire node per
     * selected source, and this helper drives exactly one of them:
     * one process writes one rawvideo stream to one socket for one
     * ffmpeg. Picking N>1 in the dialog therefore has no meaningful
     * interpretation here, and silently taking the first made the
     * source look broken with no explanation. Capture N windows by
     * adding N Window sources instead - each gets its own session and
     * its own dialog. */
    uint32_t stream_count = 0;
    while (g_variant_iter_next(&iter, "@(ua{sv})", &stream)) {
        if (stream_count == 0) {
            g_variant_get(stream, "(u@a{sv})", &g_pw_node_id, NULL);
        }
        stream_count++;
        g_variant_unref(stream);
    }
    if (stream_count > 1) {
        /* We already ask the portal for a single source: libportal sends
         * "multiple: false" whenever XDP_SCREENCAST_FLAG_MULTIPLE is not
         * set (lp.c: call->multiple = flags & XDP_SCREENCAST_FLAG_MULTIPLE),
         * and there is no other option in the SelectSources dict that
         * constrains the count. GNOME's picker still lets more than one be
         * chosen, so the extra streams cannot be prevented from here.
         *
         * Refusing outright turned a usable capture into no capture at all,
         * which is worse: one source capturing one window is still a
         * correct result. Take the first, say plainly that the rest are
         * ignored, and point at the supported way to record more. */
        fprintf(stderr,
            "cs-pw-helper: warning: the Share dialog returned %u streams. This "
            "helper records one source to one socket, so only the first will be "
            "captured and the rest are ignored. To record several windows, add "
            "one Window source per window - each gets its own Share dialog.\n",
            stream_count);
    }
    if (g_pw_node_id == 0) {
        fprintf(stderr, "cs-pw-helper: probe: no streams available yet\n");
        return TRUE;
    }
    fprintf(stderr, "cs-pw-helper: probe: using source node id %u (%u stream%s)\n",
            g_pw_node_id, stream_count, stream_count == 1 ? "" : "s");
    /* setup_pw_stream uses PW APIs and must run on the PW
     * thread. schedule_pw_setup writes the node_id to
     * g_setup_node_id, sets g_setup_pending=1, and signals
     * the PW loop. The PW thread's on_signal_event callback
     * runs setup_pw_stream_on_pw_thread on the right thread. */
    schedule_pw_setup(g_pw_node_id);
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

static void begin_screencast_session(void);

/* Number of sources the user picked in the Share dialog. The node ids come
 * back on the Start response, so this is available before any PipeWire
 * setup - which is where we want it, so a bad selection costs a dialog
 * rather than a half-built stream. */
static uint32_t count_session_streams(void) {
    if (!g_session) return 0;
    GVariant *streams = xdp_session_get_streams(g_session);
    if (!streams) return 0;
    GVariantIter iter;
    uint32_t n = 0;
    if (g_variant_iter_init(&iter, streams)) {
        GVariant *s;
        while (g_variant_iter_next(&iter, "@(ua{sv})", &s)) { n++; g_variant_unref(s); }
    }
    return n;
}

/* Explain, then let the user retry. A warning rather than an error
 * because nothing has been recorded yet and the fix is a click away. */
static void explain_multi_source(uint32_t n) {
    GtkWidget *w = gtk_message_dialog_new(
        NULL, GTK_DIALOG_MODAL, GTK_MESSAGE_WARNING, GTK_BUTTONS_OK,
        "This capture source can record only one window");
    GtkMessageDialog *d = GTK_MESSAGE_DIALOG(w);
    gtk_message_dialog_format_secondary_text(
        d,
        "You chose %u sources in the Share dialog.\n\n"
        "Nothing has been recorded. Pick a single window or monitor when the "
        "next dialog opens.\n\n"
        "To record more than one window at the same time, add another Window "
        "source in CaptureSpike. Each source gets its own Share dialog, so "
        "each can record a different window.",
        n);
    gtk_window_set_title(GTK_WINDOW(w), "CaptureSpike - too many sources selected");
    /* gtk_dialog_run() blocks until the dialog is closed and returns, so
     * destroy it once here. Do not also connect "response" to
     * gtk_widget_destroy: that is a second destroy on the same widget and
     * GTK asserts on it. */
    gtk_dialog_run(GTK_DIALOG(w));
    gtk_widget_destroy(w);
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

    /* Enforce one source per capture, before touching PipeWire, so a
     * multi-selection never gets as far as a stream that could not have
     * produced anything useful. */
    uint32_t picked = count_session_streams();
    if (picked > 1) {
        if (g_source_retries < MAX_SOURCE_RETRIES) {
            g_source_retries++;
            fprintf(stderr,
                "cs-pw-helper: %u sources selected; this source records one. "
                "Re-asking (attempt %d of %d).\n",
                picked, g_source_retries, MAX_SOURCE_RETRIES);
            explain_multi_source(picked);
            xdp_session_close(g_session);
            g_clear_object(&g_session);
            fprintf(stderr, "cs-pw-helper: opening a fresh Share dialog...\n");
            begin_screencast_session();
            return;
        }
        fprintf(stderr,
            "cs-pw-helper: still %u sources selected after %d attempts. "
            "This source records exactly one; select a single window.\n",
            picked, MAX_SOURCE_RETRIES);
        if (g_main_loop) g_main_loop_quit(g_main_loop);
        exit(EXIT_MULTI_SOURCE);
    }
    if (picked == 1)
        fprintf(stderr, "cs-pw-helper: one source selected, as required\n");

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

/* Ask the portal for a source set and open the Share dialog. Called once
 * from main and again for each retry after a multi-selection. */
static void begin_screencast_session(void) {
    xdp_portal_create_screencast_session(
        g_portal,
        source_type,
        XDP_SCREENCAST_FLAG_NONE,
        include_cursor ? XDP_CURSOR_MODE_EMBEDDED : XDP_CURSOR_MODE_HIDDEN,
        XDP_PERSIST_MODE_NONE,
        NULL, NULL,
        on_create_session_finish, NULL);
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
    /* source_type and include_cursor are file-scope so that
     * begin_screencast_session() can restart the session after a
     * multi-selection; do not shadow them here. */
    int framerate = 30;

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
    g_requested_fps = framerate > 0 ? framerate : 30;

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
        // The main loop must exist before any path that might call
        // g_main_loop_quit() - notably on_stream_state_changed, the
        // multi-source retry, and the variant walk's "all failed"
        // path. It is assigned at the top of main() and only assigned
        // once; this branch is the original test-mode assignment and
        // is kept for clarity. The bug it caused when first introduced
        // was that any quit call before the assignment was a no-op
        // because the guard `if (g_main_loop)` short-circuited, and the
        // variant walk then re-armed itself forever.
        if (g_main_loop == NULL) g_main_loop = g_main_loop_new(NULL, FALSE);
    } else {
        fprintf(stderr, "cs-pw-helper: requesting screen capture (this opens a system dialog)...\n");
        /* GTK is only needed for the "you picked too many sources"
         * dialog, and only on this path. --node-id test mode skips it. */
        int argc_gtk = 0;
        gtk_init(&argc_gtk, NULL);
        // Set up the main loop before the async session flow, so the
        // variant walk's "all failed" quit, the multi-source retry's
        // failure paths, and on_stream_state_changed's ERROR handler
        // can all actually exit. Assigning it after begin_screencast_session
        // made every quit a no-op while the helper was in the middle of
        // walking variants, and the loop re-armed itself forever,
        // writing the same line to stderr ~70 million times.
        if (g_main_loop == NULL) g_main_loop = g_main_loop_new(NULL, FALSE);
        begin_screencast_session();
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

    return g_exit_code ? g_exit_code : EXIT_OK;
}
