//! Calendar boundaries are resolved by the originating platform. Tick comparisons
//! remain integral even when two events are only one Windows clock tick apart.
use crate::TokenUsage;

#[derive(Clone, Copy)]
pub struct PeriodBounds {
    pub through: i64,
    pub today: i64,
    pub week: i64,
    pub month: i64,
}

pub struct PeriodTotals {
    pub today: TokenUsage,
    pub week: TokenUsage,
    pub month: TokenUsage,
    pub all_time: TokenUsage,
    pub newest: Option<(i64, i32)>,
    pub marked: bool,
}

impl Default for PeriodTotals {
    fn default() -> Self {
        Self {
            today: TokenUsage::ZERO,
            week: TokenUsage::ZERO,
            month: TokenUsage::ZERO,
            all_time: TokenUsage::ZERO,
            newest: None,
            marked: false,
        }
    }
}

impl PeriodTotals {
    pub fn record(
        &mut self,
        bounds: PeriodBounds,
        at: i64,
        index: i32,
        usage: TokenUsage,
        repaired: bool,
    ) {
        if at > bounds.through {
            return;
        }
        self.all_time = self.all_time.adding(usage);
        if at >= bounds.today {
            self.today = self.today.adding(usage);
        }
        if at >= bounds.week {
            self.week = self.week.adding(usage);
        }
        if at >= bounds.month {
            self.month = self.month.adding(usage);
        }
        self.marked |= repaired && at >= bounds.week.min(bounds.month);
        if self.newest.is_none_or(|(time, _)| at > time) {
            self.newest = Some((at, index));
        }
    }
}
