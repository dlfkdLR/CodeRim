//! The desktop relay exports at most 64 sessions. Keep only that prefix while
//! scanning. Titles, provider IDs and local session data stay in the host.
use std::cmp::Ordering;
use std::collections::BinaryHeap;
use std::slice;

const LIMIT: usize = 64;
const MAX_SESSIONS: u32 = 1_000_000;
#[repr(C)]
pub struct SessionKey {
    since: f64,
    rank: u32,
    reserved: u32,
}

#[derive(Clone, Copy, PartialEq)]
struct Entry {
    rank: u32,
    since: f64,
    index: u32,
}

// Entries only contain checked timestamps. Numeric order makes signed zeros equal.
impl Eq for Entry {}

impl Ord for Entry {
    fn cmp(&self, other: &Self) -> Ordering {
        self.rank
            .cmp(&other.rank)
            .then_with(|| {
                other
                    .since
                    .partial_cmp(&self.since)
                    .unwrap_or(Ordering::Equal)
            })
            .then_with(|| self.index.cmp(&other.index))
    }
}

impl PartialOrd for Entry {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

/// # Safety
/// Nonempty arrays must have the declared length, valid alignment and no overlap.
/// The caller keeps the input immutable until this synchronous function returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_mobile_session_order(
    keys: *const SessionKey,
    count: u32,
    indices: *mut u32,
    capacity: u32,
) -> u32 {
    let limit = (count as usize).min(LIMIT);
    if count == 0 {
        return 0;
    }
    if count > MAX_SESSIONS || (capacity as usize) < limit || keys.is_null() || indices.is_null() {
        return u32::MAX;
    }
    // SAFETY: the host supplies a bounded array of these fixed-width keys.
    let keys = unsafe { slice::from_raw_parts(keys, count as usize) };
    let mut best = BinaryHeap::with_capacity(limit);
    for (index, key) in keys.iter().enumerate() {
        if key.since.is_nan() || key.rank > 3 || key.reserved != 0 {
            return u32::MAX;
        }
        let row = Entry {
            rank: key.rank,
            since: key.since,
            index: index as u32,
        };
        if best.len() < limit {
            best.push(row);
        } else if let Some(mut worst) = best.peek_mut()
            && row < *worst
        {
            *worst = row;
        }
    }
    // SAFETY: validate the entire input before writing any output.
    let output = unsafe { slice::from_raw_parts_mut(indices, limit) };
    for (slot, row) in output.iter_mut().zip(best.into_sorted_vec()) {
        *slot = row.index;
    }
    limit as u32
}

#[cfg(test)]
mod tests {
    use super::*;
    fn run(values: &[SessionKey], cap: u32) -> (u32, [u32; LIMIT]) {
        let mut out = [u32::MAX; LIMIT];
        // SAFETY: separate arrays stay alive through the call.
        let len = unsafe {
            cr_mobile_session_order(values.as_ptr(), values.len() as u32, out.as_mut_ptr(), cap)
        };
        (len, out)
    }
    fn key(rank: u32, since: f64) -> SessionKey {
        SessionKey {
            since,
            rank,
            reserved: 0,
        }
    }
    #[test]
    fn ties_keep_original_order_at_the_boundary() {
        let mut keys: Vec<_> = (0..130).map(|_| key(1, 4.0)).collect();
        keys[129] = key(0, -20.0);
        let (len, result) = run(&keys, 64);
        assert_eq!(len, 64);
        assert_eq!(result[0], 129);
        assert!(result[1..].iter().copied().eq(0..63));
    }
    #[test]
    fn timestamp_extremes_and_zero_use_numeric_order() {
        let values = [
            f64::NEG_INFINITY,
            -0.,
            f64::INFINITY,
            0.,
            -1.,
            1.,
            f64::MIN_POSITIVE,
            -f64::MIN_POSITIVE,
        ];
        let keys: Vec<_> = values.map(|x| key(0, x)).into();
        let (n, out) = run(&keys, 8);
        assert_eq!(n, 8);
        assert_eq!(out[..8], [2, 5, 6, 1, 3, 7, 4, 0]);
    }
    #[test]
    fn invalid_input_preserves_destination() {
        for (rows, cap) in [
            (vec![key(1, 5.0), key(0, f64::NAN)], 2),
            (vec![key(0, 0.0)], 0),
            (vec![key(4, 0.)], 1),
            (
                vec![SessionKey {
                    rank: 0,
                    since: 1.,
                    reserved: 1,
                }],
                1,
            ),
        ] {
            assert_eq!(run(&rows, cap), (u32::MAX, [u32::MAX; LIMIT]));
        }
        assert_eq!(run(&[], 0).0, 0);
    }
}
