#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define LISTEN_PORT 9999
#define LISTEN_BACKLOG 16384
#define MAX_EVENTS 1024
#define MAX_FDS 65536
#define BUFFER_SIZE 4096

typedef struct Conn Conn;

typedef struct {
    Conn *conn;
    int side;
} FdRef;

struct Conn {
    int client_fd;
    int backend_fd;
    int backend_connecting;
    char c2b[BUFFER_SIZE];
    size_t c2b_off;
    size_t c2b_len;
    char b2c[BUFFER_SIZE];
    size_t b2c_off;
    size_t b2c_len;
};

static const char *backend_paths[] = {"/sockets/api1.sock", "/sockets/api2.sock"};
static FdRef fd_refs[MAX_FDS];
static int epoll_fd;
static unsigned int next_backend;

static void close_conn(Conn *conn);

static int set_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0) return -1;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

static void set_limits(void) {
    struct rlimit limit = {65535, 65535};
    setrlimit(RLIMIT_NOFILE, &limit);
}

static int create_listener(void) {
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;

    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(LISTEN_PORT);

    if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        close(fd);
        return -1;
    }

    if (listen(fd, LISTEN_BACKLOG) < 0) {
        close(fd);
        return -1;
    }

    return fd;
}

static int connect_backend(int *connecting) {
    const char *path = backend_paths[next_backend++ & 1U];
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;

    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, path, sizeof(addr.sun_path) - 1);

    int rc = connect(fd, (struct sockaddr *)&addr, sizeof(addr));
    if (rc == 0) {
        *connecting = 0;
        return fd;
    }

    if (errno == EINPROGRESS || errno == EAGAIN) {
        *connecting = 1;
        return fd;
    }

    close(fd);
    return -1;
}

static void compact_buffer(char *buffer, size_t *off, size_t *len) {
    if (*off == 0 || *len == 0) return;
    memmove(buffer, buffer + *off, *len);
    *off = 0;
}

static int write_buffer(int fd, char *buffer, size_t *off, size_t *len) {
    while (*len > 0) {
        ssize_t sent = send(fd, buffer + *off, *len, MSG_NOSIGNAL);
        if (sent > 0) {
            *off += (size_t)sent;
            *len -= (size_t)sent;
            if (*len == 0) *off = 0;
            continue;
        }

        if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) return 0;
        return -1;
    }

    return 0;
}

static int read_into_buffer(int fd, char *buffer, size_t *off, size_t *len) {
    compact_buffer(buffer, off, len);
    while (*len < BUFFER_SIZE) {
        ssize_t got = recv(fd, buffer + *len, BUFFER_SIZE - *len, 0);
        if (got > 0) {
            *len += (size_t)got;
            continue;
        }

        if (got == 0) return -1;
        if (errno == EAGAIN || errno == EWOULDBLOCK) return 0;
        return -1;
    }

    return 0;
}

static void clear_ref(int fd) {
    if (fd >= 0 && fd < MAX_FDS) {
        fd_refs[fd].conn = NULL;
        fd_refs[fd].side = 0;
    }
}

static int set_ref(int fd, Conn *conn, int side) {
    if (fd < 0 || fd >= MAX_FDS) return -1;
    fd_refs[fd].conn = conn;
    fd_refs[fd].side = side;
    return 0;
}

static int update_events_for_fd(int fd) {
    if (fd < 0 || fd >= MAX_FDS) return -1;
    FdRef ref = fd_refs[fd];
    Conn *conn = ref.conn;
    if (conn == NULL) return -1;

    uint32_t events = EPOLLERR | EPOLLHUP | EPOLLRDHUP;
    if (ref.side == 0) {
        if (conn->c2b_len < BUFFER_SIZE) events |= EPOLLIN;
        if (conn->b2c_len > 0) events |= EPOLLOUT;
    } else {
        if (conn->backend_connecting) {
            events |= EPOLLOUT;
        } else {
            if (conn->b2c_len < BUFFER_SIZE) events |= EPOLLIN;
            if (conn->c2b_len > 0) events |= EPOLLOUT;
        }
    }

    struct epoll_event ev;
    memset(&ev, 0, sizeof(ev));
    ev.events = events;
    ev.data.fd = fd;
    return epoll_ctl(epoll_fd, EPOLL_CTL_MOD, fd, &ev);
}

static int add_fd(int fd, Conn *conn, int side, uint32_t events) {
    if (set_ref(fd, conn, side) < 0) return -1;

    struct epoll_event ev;
    memset(&ev, 0, sizeof(ev));
    ev.events = events | EPOLLERR | EPOLLHUP | EPOLLRDHUP;
    ev.data.fd = fd;
    if (epoll_ctl(epoll_fd, EPOLL_CTL_ADD, fd, &ev) < 0) {
        clear_ref(fd);
        return -1;
    }

    return 0;
}

static void close_conn(Conn *conn) {
    if (conn == NULL) return;

    if (conn->client_fd >= 0) {
        epoll_ctl(epoll_fd, EPOLL_CTL_DEL, conn->client_fd, NULL);
        clear_ref(conn->client_fd);
        close(conn->client_fd);
    }

    if (conn->backend_fd >= 0) {
        epoll_ctl(epoll_fd, EPOLL_CTL_DEL, conn->backend_fd, NULL);
        clear_ref(conn->backend_fd);
        close(conn->backend_fd);
    }

    free(conn);
}

static void accept_clients(int listen_fd) {
    for (;;) {
        int client_fd = accept4(listen_fd, NULL, NULL, SOCK_NONBLOCK | SOCK_CLOEXEC);
        if (client_fd < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) return;
            return;
        }

        int one = 1;
        setsockopt(client_fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));

        int connecting = 0;
        int backend_fd = connect_backend(&connecting);
        if (backend_fd < 0) {
            close(client_fd);
            continue;
        }

        Conn *conn = calloc(1, sizeof(Conn));
        if (conn == NULL) {
            close(client_fd);
            close(backend_fd);
            continue;
        }

        conn->client_fd = client_fd;
        conn->backend_fd = backend_fd;
        conn->backend_connecting = connecting;

        if (add_fd(client_fd, conn, 0, EPOLLIN) < 0 ||
            add_fd(backend_fd, conn, 1, connecting ? EPOLLOUT : EPOLLIN) < 0) {
            close_conn(conn);
            continue;
        }
    }
}

static int finish_backend_connect(Conn *conn) {
    int err = 0;
    socklen_t len = sizeof(err);
    if (getsockopt(conn->backend_fd, SOL_SOCKET, SO_ERROR, &err, &len) < 0) return -1;
    if (err != 0) return -1;
    conn->backend_connecting = 0;
    return 0;
}

static void handle_fd(int fd, uint32_t events) {
    if (fd < 0 || fd >= MAX_FDS) return;
    FdRef ref = fd_refs[fd];
    Conn *conn = ref.conn;
    if (conn == NULL) return;

    if (events & (EPOLLERR | EPOLLHUP | EPOLLRDHUP)) {
        close_conn(conn);
        return;
    }

    if (ref.side == 1 && conn->backend_connecting && (events & EPOLLOUT)) {
        if (finish_backend_connect(conn) < 0) {
            close_conn(conn);
            return;
        }
    }

    if (events & EPOLLOUT) {
        int rc = ref.side == 0
            ? write_buffer(fd, conn->b2c, &conn->b2c_off, &conn->b2c_len)
            : write_buffer(fd, conn->c2b, &conn->c2b_off, &conn->c2b_len);
        if (rc < 0) {
            close_conn(conn);
            return;
        }
    }

    if (events & EPOLLIN) {
        int rc = ref.side == 0
            ? read_into_buffer(fd, conn->c2b, &conn->c2b_off, &conn->c2b_len)
            : read_into_buffer(fd, conn->b2c, &conn->b2c_off, &conn->b2c_len);
        if (rc < 0) {
            close_conn(conn);
            return;
        }
    }

    update_events_for_fd(conn->client_fd);
    update_events_for_fd(conn->backend_fd);
}

int main(void) {
    signal(SIGPIPE, SIG_IGN);
    set_limits();

    int listen_fd = create_listener();
    if (listen_fd < 0) {
        perror("listen");
        return 1;
    }

    epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    if (epoll_fd < 0) {
        perror("epoll_create1");
        return 1;
    }

    struct epoll_event ev;
    memset(&ev, 0, sizeof(ev));
    ev.events = EPOLLIN | EPOLLERR | EPOLLHUP;
    ev.data.fd = listen_fd;
    if (epoll_ctl(epoll_fd, EPOLL_CTL_ADD, listen_fd, &ev) < 0) {
        perror("epoll_ctl listen");
        return 1;
    }

    fprintf(stderr, "rinha-lb listening on :%d\n", LISTEN_PORT);

    struct epoll_event events[MAX_EVENTS];
    for (;;) {
        int n = epoll_wait(epoll_fd, events, MAX_EVENTS, -1);
        if (n < 0) {
            if (errno == EINTR) continue;
            perror("epoll_wait");
            return 1;
        }

        for (int i = 0; i < n; i++) {
            int fd = events[i].data.fd;
            if (fd == listen_fd) {
                accept_clients(listen_fd);
            } else {
                handle_fd(fd, events[i].events);
            }
        }
    }
}
