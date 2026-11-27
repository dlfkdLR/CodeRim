//! Daily relay usage and reservation policy. The host owns SQLite and samples
//! each clock at the same point as the public relay (including across midnight).
use core::ptr::addr_of_mut;

#[repr(C)]
#[derive(Clone, Copy)]
struct Counters {
    requests: f64,
    writes: f64,
    reads: f64,
}

// Output only, immediately copied by the synchronous Worker adapter. There are
// no threads, imported callbacks, or references surviving an exported call.
const ZERO: Counters = Counters {
    requests: 0.0,
    writes: 0.0,
    reads: 0.0,
};
static mut OUTPUT: [Counters; 2] = [ZERO; 2];

#[unsafe(no_mangle)]
pub extern "C" fn budget_output() -> *mut f64 {
    addr_of_mut!(OUTPUT).cast()
}

fn output(requests: f64, writes: f64, reads: f64) {
    // SAFETY: only this single-threaded module writes the fixed output buffer.
    unsafe {
        (*addr_of_mut!(OUTPUT))[0] = Counters {
            requests,
            writes,
            reads,
        };
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn budget_record(
    today: f64,
    day: f64,
    requests: f64,
    writes: f64,
    reads: f64,
    request: bool,
    write_delta: f64,
    read_delta: f64,
) -> bool {
    let rollover = day != today;
    let (requests, writes, reads) = if rollover {
        (0.0, 0.0, 0.0)
    } else {
        (requests, writes, reads)
    };
    output(
        if request { requests + 1.0 } else { requests },
        writes + write_delta,
        reads + read_delta,
    );
    rollover
}

#[unsafe(no_mangle)]
pub extern "C" fn budget_level(
    requests: f64,
    writes: f64,
    reads: f64,
    request_limit: f64,
    write_limit: f64,
    read_limit: f64,
) -> u32 {
    let requests = requests / request_limit;
    let writes = writes / write_limit;
    let reads = if read_limit != 0.0 {
        reads / read_limit
    } else {
        0.0
    };
    // Math.max in the original policy propagates any NaN (such as 0 / 0).
    if requests.is_nan() || writes.is_nan() || reads.is_nan() {
        return 0;
    }
    let ratio = requests.max(writes).max(reads);
    if ratio >= 0.9 {
        2
    } else if ratio >= 0.7 {
        1
    } else {
        0
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn budget_reserve(
    day: f64,
    requests: f64,
    writes: f64,
    reads: f64,
    today: f64,
    request_limit: f64,
    write_limit: f64,
    read_limit: f64,
    saved_day: f64,
    saved_requests: f64,
    saved_writes: f64,
    saved_reads: f64,
    saved_level: u32,
) -> u32 {
    let level = if today == day {
        budget_level(
            requests,
            writes,
            reads,
            request_limit,
            write_limit,
            read_limit,
        )
    } else {
        0
    };
    if day == saved_day
        && level == saved_level
        && requests <= saved_requests
        && writes <= saved_writes
        && reads <= saved_reads
    {
        return 0;
    }
    // SAFETY: same fixed, synchronous output as budget_record, in its own slot.
    unsafe {
        (*addr_of_mut!(OUTPUT))[1] = Counters {
            requests: requests + 20.0,
            writes: writes + 20.0,
            reads: reads + 5000.0,
        };
    }
    level + 1
}

/// System-clock calls have no callbacks into the relay, so both samples and the
/// host's current SQLite counters can be evaluated in one boundary crossing.
#[unsafe(no_mangle)]
pub extern "C" fn budget_count(
    today: f64,
    day: f64,
    requests: f64,
    writes: f64,
    reads: f64,
    request: bool,
    write_delta: f64,
    read_delta: f64,
    reserve_today: f64,
    request_limit: f64,
    write_limit: f64,
    read_limit: f64,
    saved_day: f64,
    saved_requests: f64,
    saved_writes: f64,
    saved_reads: f64,
    saved_level: u32,
) -> u32 {
    let rollover = budget_record(
        today,
        day,
        requests,
        writes,
        reads,
        request,
        write_delta,
        read_delta,
    );
    // SAFETY: budget_record initialized slot zero in the same synchronous call.
    let current = unsafe { (*addr_of_mut!(OUTPUT))[0] };
    let plan = budget_reserve(
        today,
        current.requests,
        current.writes,
        current.reads,
        reserve_today,
        request_limit,
        write_limit,
        read_limit,
        saved_day,
        saved_requests,
        saved_writes,
        saved_reads,
        saved_level,
    );
    (u32::from(rollover) << 2) | plan
}
