/// The native window owns rendering and schedules callbacks carrying these timer IDs.
#[derive(Clone, Copy, Default)]
#[repr(C)]
pub struct Interaction {
    peek_until: f64,
    serial: u64,
    fold_timer: u64,
    peek_timer: u64,
    hover_timer: u64,
    expanded: bool,
    pinned: bool,
    always_on: bool,
    menu_open: bool,
    hidden: bool,
}
const ENTER: u32 = 0;
const LEAVE: u32 = 1;
const FOLD_FIRED: u32 = 2;
const PEEK: u32 = 3;
const PEEK_ENDED_OUTSIDE: u32 = 4;
const PEEK_ENDED_INSIDE: u32 = 5;
const FOCUSED: u32 = 6;
const ALWAYS: u32 = 7;
const ON_HOVER: u32 = 8;
const HIDDEN: u32 = 9;
const TOGGLE_PIN: u32 = 10;
const MENU_OPEN: u32 = 11;
const MENU_CLOSE: u32 = 12;
const EDGE_FOLD: u32 = 13;
const EDGE_OPEN: u32 = 14;
const STOP: u32 = 15;
const CANCEL_HOVER_CLEAR: u32 = 16;
const QUEUE_HOVER_CLEAR: u32 = 17;
const HOVER_CLEAR_FIRED: u32 = 18;
const CLEAR_HOVER: u32 = 1;

impl Interaction {
    fn next_timer(&mut self) -> u64 {
        self.serial = self.serial.wrapping_add(1).max(1);
        self.serial
    }

    fn held(&self) -> bool {
        self.pinned || self.always_on || self.menu_open
    }

    fn fold(&mut self) -> u32 {
        self.hover_timer = 0;
        self.expanded = false;
        CLEAR_HOVER
    }

    fn cancel_peek(&mut self) {
        self.peek_timer = 0;
        self.peek_until = 0.0;
    }

    fn apply(&mut self, event: u32, time: f64, timer: u64) -> u32 {
        match event {
            ENTER => {
                // Pointer entered, or a plain click opens the notch.
                self.fold_timer = 0;
                self.expanded = true;
            }
            LEAVE => {
                if self.expanded
                    && !self.held()
                    && self.fold_timer == 0
                    && !(self.peek_timer != 0 && self.peek_until > time)
                {
                    self.fold_timer = self.next_timer();
                }
            }
            FOLD_FIRED if timer != 0 && self.fold_timer == timer => {
                self.fold_timer = 0;
                if !self.held() {
                    return self.fold();
                }
            }
            PEEK if !self.hidden => {
                self.fold_timer = 0;
                self.peek_until = time;
                self.peek_timer = self.next_timer();
                self.expanded = true;
            }
            PEEK_ENDED_OUTSIDE | PEEK_ENDED_INSIDE if timer != 0 && self.peek_timer == timer => {
                self.cancel_peek();
                if event == PEEK_ENDED_OUTSIDE && !self.held() {
                    return self.fold();
                }
            }
            FOCUSED => {
                self.cancel_peek();
                return self.fold();
            }
            ALWAYS | ON_HOVER | HIDDEN => {
                self.cancel_peek();
                self.fold_timer = 0;
                self.always_on = event == ALWAYS;
                self.hidden = event == HIDDEN;
                self.pinned = false;
                if !self.always_on {
                    return self.fold();
                }
                self.expanded = true;
            }
            TOGGLE_PIN if !self.always_on => {
                self.pinned = !self.pinned;
                if self.pinned {
                    self.fold_timer = 0;
                    self.expanded = true;
                }
            }
            MENU_OPEN => {
                self.fold_timer = 0;
                self.menu_open = true;
            }
            MENU_CLOSE => self.menu_open = false,
            EDGE_FOLD => self.expanded = false,
            EDGE_OPEN => self.expanded = true,
            CANCEL_HOVER_CLEAR => self.hover_timer = 0,
            QUEUE_HOVER_CLEAR if self.hover_timer == 0 => self.hover_timer = self.next_timer(),
            HOVER_CLEAR_FIRED if timer != 0 && timer == self.hover_timer => {
                self.hover_timer = 0;
                return CLEAR_HOVER;
            }
            STOP => {
                self.hover_timer = 0;
                self.cancel_peek();
                self.fold_timer = 0;
            }
            _ => {}
        }
        0
    }
}

/// # Safety
/// A non-null pointer must be an initialized, exclusively writable native session for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_notch_interact(
    session: *mut Interaction,
    event: u32,
    time: f64,
    timer_id: u64,
) -> u32 {
    unsafe { session.as_mut() }.map_or(0, |state| state.apply(event, time, timer_id))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timers_from_replaced_peeks_and_folds_cannot_close_a_new_presentation() {
        let mut s = Interaction::default();
        s.apply(PEEK, 10.0, 0);
        let initial_peek = s.peek_timer;
        s.apply(PEEK, 12.0, 0);
        let latest = s.peek_timer;
        assert_ne!(initial_peek, latest);
        assert_eq!(s.apply(PEEK_ENDED_OUTSIDE, 10.0, initial_peek), 0);
        s.apply(LEAVE, 11.9, 0);
        assert_eq!(s.fold_timer, 0);
        s.apply(PEEK_ENDED_INSIDE, 12.0, latest);
        s.apply(LEAVE, 12.0, 0);
        let old_fold = s.fold_timer;
        s.apply(ENTER, 0.0, 0);
        s.apply(LEAVE, 13.0, 0);
        assert_ne!(old_fold, s.fold_timer);
        assert_eq!(s.apply(FOLD_FIRED, 14.0, old_fold), 0);
        assert!(s.expanded);
        assert_eq!(s.apply(FOLD_FIRED, 14.0, s.fold_timer), CLEAR_HOVER);
        assert!(!s.expanded);
    }

    #[test]
    fn menu_hold_pin_and_visibility_have_independent_lifetimes() {
        let mut s = Interaction::default();
        s.apply(ENTER, 0.0, 0);
        s.apply(LEAVE, 0.0, 0);
        s.apply(MENU_OPEN, 0.0, 0);
        assert_eq!(s.fold_timer, 0);
        s.apply(TOGGLE_PIN, 0.0, 0);
        s.apply(MENU_CLOSE, 0.0, 0);
        s.apply(LEAVE, 0.0, 0);
        assert_eq!(s.fold_timer, 0);
        s.apply(ALWAYS, 0.0, 0);
        assert!(!s.pinned && s.always_on && s.expanded);
        s.apply(TOGGLE_PIN, 0.0, 0);
        assert!(!s.pinned);
        s.apply(HIDDEN, 0.0, 0);
        s.apply(PEEK, 5.0, 0);
        assert!(!s.expanded && s.peek_timer == 0);
        s.apply(ON_HOVER, 0.0, 0);
        s.apply(ENTER, 0.0, 0);
        s.apply(LEAVE, 5.0, 0);
        let fold = s.fold_timer;
        s.apply(STOP, 0.0, 0);
        s.apply(FOLD_FIRED, 6.0, fold);
        assert!(s.expanded);
    }
    #[test]
    fn tooltip_grace_is_per_window_and_ignores_callbacks_after_reentry_or_stop() {
        let mut first = Interaction::default();
        let mut other = Interaction::default();
        first.apply(QUEUE_HOVER_CLEAR, 0.0, 0);
        let canceled = first.hover_timer;
        first.apply(QUEUE_HOVER_CLEAR, 0.0, 0);
        assert_eq!(first.hover_timer, canceled);
        first.apply(CANCEL_HOVER_CLEAR, 0.0, 0);
        first.apply(QUEUE_HOVER_CLEAR, 0.0, 0);
        other.apply(QUEUE_HOVER_CLEAR, 0.0, 0);
        assert_eq!(first.apply(HOVER_CLEAR_FIRED, 0.0, canceled), 0);
        assert_ne!(first.hover_timer, canceled);
        assert_eq!(
            other.apply(HOVER_CLEAR_FIRED, 0.0, other.hover_timer),
            CLEAR_HOVER
        );
        let stopped = first.hover_timer;
        first.apply(STOP, 0.0, 0);
        assert_eq!(first.apply(HOVER_CLEAR_FIRED, 0.0, stopped), 0);
    }
}
