#ifndef AGENT_SPEED_TRAFFIC_IDLE_CLOCK_H
#define AGENT_SPEED_TRAFFIC_IDLE_CLOCK_H

#include <stdint.h>
#include <time.h>

#define TRAFFIC_HELPER_IDLE_TIMEOUT_SECONDS 120ULL

static inline int traffic_helper_boottime(struct timespec *now)
{
    return clock_gettime(CLOCK_BOOTTIME, now);
}

static inline int traffic_helper_boottime_seconds(uint64_t *seconds)
{
    struct timespec now;
    if (traffic_helper_boottime(&now) != 0) return -1;
    *seconds = (uint64_t)now.tv_sec;
    return 0;
}

static inline int traffic_helper_idle_expired(uint64_t last_activity, uint64_t now)
{
    return now < last_activity || now - last_activity >= TRAFFIC_HELPER_IDLE_TIMEOUT_SECONDS;
}

#endif
