//! Group Windows Settings history into its daily, model and session projections.
//! The host retains identifier spelling, calendar transitions and date offsets.
use std::slice;

#[repr(C)]
#[derive(Clone, Copy)]
struct Event {
    at: i64,
    input: i64,
    cached: i64,
    output: i64,
    written: i64,
    model: i32,
    session: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Group {
    input: i64,
    cached: i64,
    output: i64,
    written: i64,
    latest_plus_one: i64,
}

impl Group {
    fn add(&mut self, event: &Event) {
        self.input = self.input.saturating_add(event.input);
        self.cached = self.cached.saturating_add(event.cached);
        self.output = self.output.saturating_add(event.output);
        self.written = if self.written < 0 || event.written < 0 {
            -1
        } else {
            self.written.saturating_add(event.written)
        };
    }
}

/// # Safety
/// The caller supplies initialized disjoint, aligned spans of the declared lengths,
/// exclusively owns the result span and pins all three for this synchronous call.
#[unsafe(no_mangle)]
unsafe extern "C" fn cr_windows_settings_history_v1(
    events: *const Event,
    count: usize,
    inclusive_ends: *const i64,
    days: usize,
    models: usize,
    sessions: usize,
    output: *mut Group,
    groups: usize,
) -> i32 {
    if count == 0
        || count > 1_000_000
        || days == 0
        || days > 32
        || models == 0
        || sessions == 0
        || models > count
        || sessions > count
        || days * models > 65_536
        || groups != 1 + days + models + sessions + days * models
        || events.is_null()
        || inclusive_ends.is_null()
        || output.is_null()
    {
        return 0;
    }
    let events = unsafe { slice::from_raw_parts(events, count) };
    let ends = unsafe { slice::from_raw_parts(inclusive_ends, days) };
    let groups = unsafe { slice::from_raw_parts_mut(output, groups) };
    if !ends.is_sorted() {
        return 0;
    }
    groups.fill(Group::default());
    let model_start = 1 + days;
    let session_start = model_start + models;
    let cell_start = session_start + sessions;
    for (index, row) in events.iter().enumerate() {
        let model = row.model as usize;
        let session = row.session as usize;
        if model >= models
            || session >= sessions
            || row.input < 0
            || row.cached < 0
            || row.output < 0
            || row.written < -1
        {
            return 0;
        }
        groups[0].add(row);
        groups[model_start + model].add(row);
        let activity = &mut groups[session_start + session];
        activity.add(row);
        // Equal instants retain the first event's original offset. .NET's stable
        // OrderBy followed by Max has the same tie behavior.
        if activity.latest_plus_one == 0
            || row.at > events[activity.latest_plus_one as usize - 1].at
        {
            activity.latest_plus_one = index as i64 + 1;
        }
        let day = ends.partition_point(|end| *end < row.at);
        if day < days {
            groups[1 + day].add(row);
            let cell = &mut groups[cell_start + day * models + model];
            cell.add(row);
            cell.latest_plus_one = 1; // Zero usage still creates a visible model row.
        }
    }
    1
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(at: i64, input: i64, written: i64, model: i32, session: i32) -> Event {
        Event {
            at,
            input,
            cached: 1,
            output: 1,
            written,
            model,
            session,
        }
    }
    #[test]
    fn inclusive_days_presence_and_session_ties() {
        let rows = [
            event(9, i64::MAX, 0, 0, 0),
            event(9, 1, -1, 1, 0),
            event(1, 1, 3, 0, 1),
            event(21, 3, 0, 1, 1),
        ];
        let ends = [1, 9, 20];
        let mut groups = [Group::default(); 14];
        let status = unsafe {
            cr_windows_settings_history_v1(
                rows.as_ptr(),
                rows.len(),
                ends.as_ptr(),
                3,
                2,
                2,
                groups.as_mut_ptr(),
                groups.len(),
            )
        };
        assert_eq!(status, 1);
        assert_eq!(groups[0].input, i64::MAX);
        assert_eq!(groups[0].written, -1);
        assert_eq!(groups[1].input, 1);
        assert_eq!(groups[2].input, i64::MAX);
        assert_eq!(groups[3].latest_plus_one, 0);
        assert_eq!(groups[6].latest_plus_one, 1);
        assert_eq!(groups[7].latest_plus_one, 4);
        assert_eq!(groups[8].latest_plus_one, 1);
        assert_eq!(groups[9].latest_plus_one, 0);
        assert_eq!(groups[10].latest_plus_one, 1);
        assert_eq!(groups[11].latest_plus_one, 1);
        assert_eq!(groups[12].input, 0);
    }
    #[test]
    fn rejects_unrepresentable_shapes() {
        let ends = [1, 2];
        let mut groups = [Group::default(); 7];
        let mut row = event(1, 1, 0, 0, 0);
        for (model, written, valid) in
            [(1, 0, false), (-1, 0, false), (0, -2, false), (0, -1, true)]
        {
            row.model = model;
            row.written = written;
            let accepted = unsafe {
                cr_windows_settings_history_v1(
                    &row,
                    1,
                    ends.as_ptr(),
                    2,
                    1,
                    1,
                    groups.as_mut_ptr(),
                    groups.len(),
                )
            };
            assert_eq!(accepted == 1, valid);
        }
        for (days, models, sessions, group_count) in [
            (usize::MAX, 1, 1, 7),
            (2, usize::MAX, 1, 7),
            (2, 1, usize::MAX, 7),
            (2, 1, 1, 0),
        ] {
            let accepted = unsafe {
                cr_windows_settings_history_v1(
                    &row,
                    1,
                    ends.as_ptr(),
                    days,
                    models,
                    sessions,
                    groups.as_mut_ptr(),
                    group_count,
                )
            };
            assert_eq!(accepted, 0);
        }
    }
}
