#define _GNU_SOURCE
#include "../traffic-helper/idle_clock.h"
#include <assert.h>

int main(void)
{
    assert(!traffic_helper_idle_expired(10, 129));
    assert(traffic_helper_idle_expired(10, 130));
    assert(traffic_helper_idle_expired(130, 129));

    uint64_t before, after;
    assert(traffic_helper_boottime_seconds(&before) == 0);
    assert(traffic_helper_boottime_seconds(&after) == 0);
    assert(after >= before);
    return 0;
}
