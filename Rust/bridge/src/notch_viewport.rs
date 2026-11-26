//! Provider capacity on the usable notch axis. AppKit supplies its measured font and layout values.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct Viewport {
    along: f64,
    scale: f64,
    pad_start: f64,
    pad_end: f64,
    cell: f64,
    spacing: f64,
    flare: f64,
    reserve: f64,
    wanted: f64,
    corner: f64,
    corner_offset: f64,
    control_spacing: f64,
    control_radius: f64,
    joined: u8,
}

impl Viewport {
    fn drawn(self, count: u32) -> f64 {
        self.pad_start
            + f64::from(count) * self.cell
            + f64::from(count - 1) * self.spacing
            + self.pad_end
            + 2.0 * self.flare
    }

    fn fits(self, count: u32) -> bool {
        let drawn = self.drawn(count);
        let (length, trailing) = if self.joined != 0 {
            let length = drawn + 2.0 * ((self.wanted - drawn) / 2.0).max(0.0);
            (
                length,
                length - self.flare - self.corner + self.corner_offset,
            )
        } else {
            (drawn, drawn)
        };
        let controls = (trailing - length + self.control_spacing + self.control_radius)
            .max(0.0)
            .ceil();
        length * self.scale + self.reserve.max(2.0 * controls * self.scale) <= self.along
    }
}

/// Returns zero for inputs requiring native comparison. This value-only interface borrows nothing.
#[unsafe(no_mangle)]
pub extern "C" fn cr_notch_visible_capacity(v: Viewport, available: u32) -> u32 {
    let values = [
        v.along,
        v.scale,
        v.pad_start,
        v.pad_end,
        v.cell,
        v.spacing,
        v.flare,
        v.reserve,
        v.wanted,
        v.corner,
        v.corner_offset,
        v.control_spacing,
        v.control_radius,
    ];
    if available > 1_000_000
        || v.joined > 1
        || v.along <= 0.0
        || v.scale <= 0.0
        || v.cell <= 0.0
        || values
            .iter()
            .any(|&x| !x.is_finite() || x < 0.0 || x > 1.0e12)
    {
        return 0;
    }
    let count = available.max(1);
    if v.fits(count) {
        return count;
    }
    // The body plus the fixed reserve is a lower bound at every count. Search that
    // monotone bound, then evaluate the complete hardware and control arithmetic
    // in descending order, preserving rounding at tight screen boundaries.
    let mut lower = 0;
    let mut upper = count;
    while lower < upper {
        let mid = lower + (upper - lower).div_ceil(2);
        if v.drawn(mid) * v.scale + v.reserve <= v.along {
            lower = mid;
        } else {
            upper = mid - 1;
        }
    }
    (2..=lower).rev().find(|&n| v.fits(n)).unwrap_or(1)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn measurement() -> Viewport {
        Viewport {
            along: 720.0,
            scale: 1.0,
            pad_start: 3.1,
            pad_end: 3.2,
            cell: 44.0,
            spacing: 7.5,
            flare: 2.5,
            reserve: 200.0,
            wanted: 220.3,
            corner: 8.2,
            corner_offset: 7.4,
            control_spacing: 48.0,
            control_radius: 13.0,
            joined: 1,
        }
    }

    #[test]
    fn bound_keeps_every_exact_fit() {
        for mut v in [
            measurement(),
            Viewport {
                joined: 0,
                ..measurement()
            },
        ] {
            for scale in [0.8, 1.0, 1.25] {
                v.scale = scale;
                for i in 1..=1000 {
                    v.along = f64::from(i) * 5.1;
                    let naive = (2..=100).rev().find(|&n| v.fits(n)).unwrap_or(1);
                    assert_eq!(cr_notch_visible_capacity(v, 100), naive);
                }
            }
        }
    }

    #[test]
    fn invalid_values_request_the_host_comparison() {
        let mut v = measurement();
        assert_eq!(cr_notch_visible_capacity(v, 0), 1);
        assert_eq!(cr_notch_visible_capacity(v, u32::MAX), 0);
        v.scale = f64::NAN;
        assert_eq!(cr_notch_visible_capacity(v, 70), 0);
    }
}
