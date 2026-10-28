// SPDX-License-Identifier: MIT
#define _GNU_SOURCE
#include <bpf/libbpf.h>
#include <bpf/bpf.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <sys/types.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <time.h>
#include "idle_clock.h"
#include "traffic.skel.h"

struct identity { uint32_t uid, pid; uint64_t start_ticks; };
struct counters { uint64_t rx, tx; };
static volatile sig_atomic_t stopping;
static void stop(int signal_number) { (void)signal_number; stopping = 1; }
static uint64_t boot_seconds(void)
{
    FILE *file = fopen("/proc/stat", "re"); if (!file) return 0;
    char *line = NULL; size_t capacity = 0; unsigned long long boot = 0;
    while (getline(&line, &capacity, file) > 0) if (sscanf(line, "btime %llu", &boot) == 1) break;
    free(line); fclose(file); return boot;
}
static int serve(int client, int map, uint64_t boot, uint64_t ticks, uint64_t epoch)
{
    struct ucred peer; socklen_t size = sizeof(peer);
    if (getsockopt(client, SOL_SOCKET, SO_PEERCRED, &peer, &size)) return -1;
    struct timeval timeout = { .tv_sec = 2 };
    setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
    FILE *output = fdopen(dup(client), "w"); if (!output) return -1;
    fprintf(output, "AGENT_SPEED_TRAFFIC_1 %llu\n", (unsigned long long)epoch);
    struct identity key, next; void *previous = NULL; size_t count = 0;
    while (!bpf_map_get_next_key(map, previous, &next) && count++ < 131072) {
        key = next; previous = &key;
        if (key.uid != peer.uid) continue; // The client cannot choose another UID or PID filter.
        struct counters value;
        if (!bpf_map_lookup_elem(map, &key, &value)) {
            uint64_t start = boot * 1000000ULL + key.start_ticks / ticks * 1000000ULL + key.start_ticks % ticks * 1000000ULL / ticks;
            fprintf(output, "%u %llu %llu %llu\n", key.pid, (unsigned long long)start,
                (unsigned long long)value.rx, (unsigned long long)value.tx);
        }
        if (ferror(output)) break;
    }
    fputs("END\n", output); return fclose(output);
}
int main(void)
{
    if (geteuid() != 0) { fputs("Run through the agent-speed-traffic system service.\n", stderr); return 1; }
    const char *pid = getenv("LISTEN_PID"), *fds = getenv("LISTEN_FDS");
    if (!pid || !fds || strtol(pid, NULL, 10) != getpid() || strcmp(fds, "1")) {
        fputs("Exactly one systemd-activated socket is required.\n", stderr); return 1;
    }
    signal(SIGTERM, stop); signal(SIGINT, stop); signal(SIGPIPE, SIG_IGN);
    uint64_t ticks = (uint64_t)sysconf(_SC_CLK_TCK), boot = boot_seconds();
    if (!ticks || !boot) return 1;
    struct traffic_bpf *bpf = traffic_bpf__open(); if (!bpf) return 1;
    bpf->rodata->clock_ticks = ticks;
    if (traffic_bpf__load(bpf) || traffic_bpf__attach(bpf)) {
        fputs("Cannot attach network accounting probes; kernel permissions, BTF and tracing support are required.\n", stderr);
        traffic_bpf__destroy(bpf); return 1;
    }
    struct timespec now;
    if (traffic_helper_boottime(&now) != 0) {
        perror("clock_gettime(CLOCK_BOOTTIME)");
        traffic_bpf__destroy(bpf); return 1;
    }
    uint64_t epoch = (uint64_t)now.tv_sec * 1000000000ULL + now.tv_nsec;
    uint64_t last = (uint64_t)now.tv_sec;
    while (!stopping) {
        uint64_t current;
        if (traffic_helper_boottime_seconds(&current) != 0 || traffic_helper_idle_expired(last, current)) break;
        struct pollfd socket = { .fd = 3, .events = POLLIN };
        if (poll(&socket, 1, 1000) <= 0) continue;
        int client = accept4(3, NULL, NULL, SOCK_CLOEXEC); if (client < 0) continue;
        serve(client, bpf_map__fd(bpf->maps.totals), boot, ticks, epoch);
        close(client);
        if (traffic_helper_boottime_seconds(&last) != 0) break;
        // Bound scanning work even if a local client polls faster than the application.
        struct timespec pause = { .tv_nsec = 50000000 }; nanosleep(&pause, NULL);
    }
    traffic_bpf__destroy(bpf); return 0;
}
