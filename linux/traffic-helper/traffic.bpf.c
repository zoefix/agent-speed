// SPDX-License-Identifier: (MIT OR GPL-2.0-only)
#include "vmlinux.h"
#include <bpf/bpf_helpers.h>
#include <bpf/bpf_core_read.h>
#include <bpf/bpf_tracing.h>

struct identity { __u32 uid, pid; __u64 start_ticks; };
struct counters { __u64 rx, tx; };
struct pending { struct identity identity; __u32 flags; };
const volatile __u64 clock_ticks = 100;
struct { __uint(type, BPF_MAP_TYPE_LRU_HASH); __uint(max_entries, 131072);
    __type(key, struct identity); __type(value, struct counters); } totals SEC(".maps");
struct { __uint(type, BPF_MAP_TYPE_LRU_HASH); __uint(max_entries, 16384);
    __type(key, __u64); __type(value, struct pending); } sending SEC(".maps");
struct { __uint(type, BPF_MAP_TYPE_LRU_HASH); __uint(max_entries, 16384);
    __type(key, __u64); __type(value, struct pending); } receiving SEC(".maps");

static __always_inline int enter(void *map, __u32 flags)
{
    __u64 tid = bpf_get_current_pid_tgid();
    struct task_struct *task = (void *)bpf_get_current_task();
    struct task_struct *leader = BPF_CORE_READ(task, group_leader);
    __u64 start = BPF_CORE_READ(leader, start_boottime);
    struct pending row = { .identity = { .uid = (__u32)bpf_get_current_uid_gid(),
        .pid = tid >> 32, .start_ticks = start / (1000000000ULL / clock_ticks) }, .flags = flags };
    bpf_map_update_elem(map, &tid, &row, BPF_ANY);
    return 0;
}
static __always_inline int leave(void *map, long result, int receive)
{
    __u64 tid = bpf_get_current_pid_tgid();
    struct pending *row = bpf_map_lookup_elem(map, &tid);
    if (row && result > 0 && !(receive && (row->flags & 2))) { // MSG_PEEK is not consumption
        struct counters zero = {};
        bpf_map_update_elem(&totals, &row->identity, &zero, BPF_NOEXIST);
        struct counters *value = bpf_map_lookup_elem(&totals, &row->identity);
        if (value) {
            if (receive) __sync_fetch_and_add(&value->rx, result);
            else __sync_fetch_and_add(&value->tx, result);
        }
    }
    bpf_map_delete_elem(map, &tid);
    return 0;
}
// These kernel functions return int. Reading the full 64-bit register turns
// a zero-extended negative errno (for example EAGAIN) into almost 4 GiB.
#define SEND(name) \
    SEC("kprobe/" #name) int BPF_KPROBE(name##_in) { return enter(&sending, 0); } \
    SEC("kretprobe/" #name) int BPF_KRETPROBE(name##_out, int result) { return leave(&sending, result, 0); }
#define RECEIVE(name) \
    SEC("kprobe/" #name) int BPF_KPROBE(name##_in, void *sk, void *msg, unsigned long len, int flags) { return enter(&receiving, flags); } \
    SEC("kretprobe/" #name) int BPF_KRETPROBE(name##_out, int result) { return leave(&receiving, result, 1); }
SEND(tcp_sendmsg)
SEND(udp_sendmsg)
SEND(udpv6_sendmsg)
RECEIVE(tcp_recvmsg)
RECEIVE(udp_recvmsg)
RECEIVE(udpv6_recvmsg)
char LICENSE[] SEC("license") = "Dual MIT/GPL";
