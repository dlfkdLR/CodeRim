//! Select the UI's history source and the effects of a profile preference change.
//! The native host retains all totals, dates, identities and OS side effects.

#[repr(u8)]
enum Source {
    Unavailable,
    Local,
    AccountWeek,
    AccountMonth,
    AccountLifetime,
}

#[repr(C)]
pub struct SyncDecision {
    enabled: u8,
    invalidate_snapshot: u8,
    schedule_refresh: u8,
}

// SAFETY: coderim_account_history.h declares the by-value scalar ABI.
#[unsafe(no_mangle)]
pub extern "C" fn cr_history_total_source(account_scope: u8, period: u8, has_profile: u8) -> u8 {
    let source = match (account_scope, period, has_profile) {
        (0, 0..=3, _) => Source::Local,
        // The server's today bucket belongs to its reporting date, not the local day.
        (1, 1, 1) => Source::AccountWeek,
        (1, 2, 1) => Source::AccountMonth,
        (1, 3, 1) => Source::AccountLifetime,
        _ => Source::Unavailable,
    };
    source as u8
}

// SAFETY: coderim_account_history.h declares this symbol and the by-value record.
#[unsafe(no_mangle)]
pub extern "C" fn cr_profile_sync_policy(
    allowed: u8,
    requested: u8,
    was_enabled: u8,
    week_start_changed: u8,
) -> SyncDecision {
    let enabled = allowed == 1 && requested == 1;
    let invalidate = enabled && week_start_changed == 1;
    SyncDecision {
        enabled: u8::from(enabled),
        invalidate_snapshot: u8::from(invalidate),
        schedule_refresh: u8::from(enabled && (was_enabled == 0 || invalidate)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn absent_account_or_unknown_tag_never_selects_local_history() {
        for period in 0..=3 {
            assert_eq!(cr_history_total_source(0, period, 0), Source::Local as u8);
            assert_eq!(
                cr_history_total_source(1, period, 0),
                Source::Unavailable as u8
            );
        }
        assert_eq!(cr_history_total_source(1, 0, 1), Source::Unavailable as u8);
        assert_eq!(cr_history_total_source(2, 1, 1), Source::Unavailable as u8);
        assert_eq!(cr_history_total_source(0, 4, 1), Source::Unavailable as u8);
    }

    #[test]
    fn calendar_change_replaces_the_snapshot_only_when_sync_is_enabled() {
        let disabled = cr_profile_sync_policy(0, 1, 1, 1);
        assert_eq!(
            (
                disabled.enabled,
                disabled.invalidate_snapshot,
                disabled.schedule_refresh
            ),
            (0, 0, 0)
        );
        let enabled = cr_profile_sync_policy(1, 1, 1, 1);
        assert_eq!(
            (
                enabled.enabled,
                enabled.invalidate_snapshot,
                enabled.schedule_refresh
            ),
            (1, 1, 1)
        );
    }
}
