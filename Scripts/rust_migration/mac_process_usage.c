/* Read process resource accounting for the isolated benchmark's child. */
#include <CoreGraphics/CoreGraphics.h>
#include <inttypes.h>
#include <libproc.h>
#include <mach/mach_time.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/resource.h>

static int visible_notches(int pid) {
    CFArrayRef windows = CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly, kCGNullWindowID);
    if (!windows) return -1;
    int count = 0;
    for (CFIndex i = 0; i < CFArrayGetCount(windows); i++) {
        CFDictionaryRef window = CFArrayGetValueAtIndex(windows, i);
        CFNumberRef owner = CFDictionaryGetValue(window, kCGWindowOwnerPID);
        CFNumberRef layer = CFDictionaryGetValue(window, kCGWindowLayer);
        int owner_value, layer_value;
        if (owner && layer && CFNumberGetValue(owner, kCFNumberIntType, &owner_value) &&
            CFNumberGetValue(layer, kCFNumberIntType, &layer_value) && owner_value == pid && layer_value == 25)
            count++;
    }
    CFRelease(windows);
    return count;
}

static int session_locked(void) {
    CFDictionaryRef session = CGSessionCopyCurrentDictionary();
    if (!session) return -1;
    CFTypeRef flag = CFDictionaryGetValue(session, CFSTR("CGSSessionScreenIsLocked"));
    int locked = flag && CFEqual(flag, kCFBooleanTrue);
    CFRelease(session);
    return locked;
}

int main(int argc, char **argv) {
    if (argc != 2) return 1;
    char *end;
    long value = strtol(argv[1], &end, 10);
    if (*end || value <= 0 || value > INT32_MAX) return 1;
    struct rusage_info_v6 usage = {0};
    mach_timebase_info_data_t timebase;
    if (proc_pid_rusage((int)value, RUSAGE_INFO_V6, (rusage_info_t *)&usage) != 0 ||
        mach_timebase_info(&timebase) != KERN_SUCCESS) return 1;
    double cpu_ms = (double)(usage.ri_user_time + usage.ri_system_time) *
                    timebase.numer / timebase.denom / 1e6;
    printf("{\"session_locked\":%d,\"visible_notches\":%d,\"cpu_ms\":%.8f,\"resident_bytes\":%" PRIu64
           ",\"footprint_bytes\":%" PRIu64 ",\"peak_footprint_bytes\":%" PRIu64
           ",\"interrupt_wakeups\":%" PRIu64 ",\"package_idle_wakeups\":%" PRIu64
           ",\"disk_read_bytes\":%" PRIu64 ",\"disk_written_bytes\":%" PRIu64
           ",\"logical_writes\":%" PRIu64 ",\"energy_nj\":%" PRIu64 "}\n",
           session_locked(), visible_notches((int)value), cpu_ms, usage.ri_resident_size, usage.ri_phys_footprint, usage.ri_lifetime_max_phys_footprint,
           usage.ri_interrupt_wkups, usage.ri_pkg_idle_wkups, usage.ri_diskio_bytesread,
           usage.ri_diskio_byteswritten, usage.ri_logical_writes, usage.ri_energy_nj);
    return 0;
}
