//! Notch card layout uses point measurements supplied by the platform.
//! Keep evaluation order in sync with the existing Core Graphics layout.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct Metrics {
    header: f64,
    header_gap: f64,
    account_block: f64,
    today_block: f64,
    full_row: f64,
    body_line: f64,
    spacing: f64,
    group_extra: f64,
    group_gap: f64,
    group_bottom: f64,
    token_block: f64,
    session_section: f64,
    session_row: f64,
    hidden_line: f64,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Content {
    windows: i64,
    compact: i64,
    groups: i64,
    sessions: i64,
    cap: i64,
    flags: u32,
    status_height: f64,
    block_height: f64,
}

const ACCOUNT: u32 = 1;
const TODAY: u32 = 2;
const BLOCK: u32 = 4;
const TOKENS: u32 = 8;

fn base(m: Metrics, c: Content) -> f64 {
    let mut height = m.header;
    if c.flags & ACCOUNT != 0 {
        height += m.account_block;
    }
    if c.flags & TODAY != 0 {
        height += m.today_block;
    }
    if c.flags & BLOCK != 0 {
        height += m.header_gap + c.block_height;
    }
    if c.windows > 0 {
        let full = i128::from(c.windows) - i128::from(c.compact);
        height += m.header_gap
            + full as f64 * m.full_row
            + c.compact as f64 * m.body_line
            + (c.windows - 1) as f64 * m.spacing;
        if c.groups > 0 {
            height += c.groups as f64 * m.group_extra;
            if c.groups > 1 {
                height += (c.groups - 1) as f64 * m.group_gap;
            }
            height += m.group_bottom;
        }
    } else {
        height += m.header_gap + c.status_height;
    }
    if c.flags & TOKENS != 0 {
        height += m.token_block;
    }
    height
}

fn with_sessions(mut height: f64, m: Metrics, shown: i64, hidden: bool) -> f64 {
    height +=
        m.session_section + shown as f64 * m.session_row + (shown - 1).max(0) as f64 * m.spacing;
    if hidden {
        height += m.hidden_line;
    }
    height
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_notch_card_height(m: Metrics, c: Content) -> f64 {
    let height = base(m, c);
    if c.sessions <= 0 {
        return height;
    }
    let shown = c.sessions.min(c.cap.max(0));
    with_sessions(height, m, shown, c.sessions > shown)
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_notch_sessions_fitting(
    m: Metrics,
    c: Content,
    budget: f64,
    ceiling: i32,
) -> i32 {
    let height = base(m, c);
    // Each additional row increases the same native line-height and spacing
    // terms. Search once over the bounded product session cap.
    let mut lo = 0i32;
    let mut hi = ceiling.max(0);
    while lo < hi {
        let mid = lo + (i64::from(hi) - i64::from(lo) + 1).div_euclid(2) as i32;
        if with_sessions(height, m, mid.into(), true) <= budget {
            lo = mid;
        } else {
            hi = mid - 1;
        }
    }
    lo
}
