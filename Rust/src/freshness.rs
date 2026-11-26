//! Companion snapshot ages use elapsed seconds in the host's timestamp epoch.

pub fn stale_timestamp(updated_at: Option<f64>, now: f64, max_age: f64) -> bool {
    updated_at.is_none_or(|date| now - date > max_age || date > now + 60.0)
}

pub fn stale_limits(
    partial: bool,
    updated_at: Option<f64>,
    max_age: f64,
    now: f64,
    reset_due: bool,
) -> bool {
    reset_due || ((!partial || updated_at.is_some()) && stale_timestamp(updated_at, now, max_age))
}
