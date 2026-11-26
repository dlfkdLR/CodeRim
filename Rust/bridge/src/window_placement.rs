//! AppKit panel placement and flipped-view handle geometry. NSScreen discovery and
//! persistent UUID comparison remain native; no display identifiers cross this ABI.
use std::slice;

#[derive(Clone, Copy, Default)]
#[repr(C)]
pub struct Point {
    x: f64,
    y: f64,
}
#[derive(Clone, Copy, Default)]
#[repr(C)]
pub struct Size {
    w: f64,
    h: f64,
}
#[derive(Clone, Copy, Default)]
#[repr(C)]
pub struct Rect {
    origin: Point,
    size: Size,
}

// Delegate CGRect's standardized extents, null/infinite behavior and union to CoreGraphics.
#[link(name = "CoreGraphics", kind = "framework")]
unsafe extern "C" {
    fn CGRectGetMinX(rect: Rect) -> f64;
    fn CGRectGetMinY(rect: Rect) -> f64;
    fn CGRectGetMidX(rect: Rect) -> f64;
    fn CGRectGetMidY(rect: Rect) -> f64;
    fn CGRectGetMaxX(rect: Rect) -> f64;
    fn CGRectGetMaxY(rect: Rect) -> f64;
    fn CGRectUnion(a: Rect, b: Rect) -> Rect;
}
const RIGHT: u32 = 0;
const LEFT: u32 = 1;
const TOP: u32 = 2;

#[unsafe(no_mangle)]
pub extern "C" fn cr_window_preferred(pin: isize, has_active: bool, count: isize) -> isize {
    if (0..count).contains(&pin) {
        pin
    } else if has_active {
        -2
    } else if count > 0 {
        0
    } else {
        -1
    }
}

fn clamp(value: f64, low: f64, high: f64) -> f64 {
    // Match Swift.min/max operand ordering for signed zero and non-finite inputs.
    if low <= high {
        let bottom = if value <= low { low } else { value };
        if high < bottom { high } else { bottom }
    } else {
        low
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_window_panel_frame(
    full: Rect,
    usable: Rect,
    size: Size,
    edge: u32,
    offset: f64,
    slack: f64,
    trailing: f64,
    leading: f64,
    keep: bool,
    hardware: bool,
) -> Rect {
    let w = size.w.ceil();
    let h = size.h.ceil();
    // SAFETY: CoreGraphics takes CGRect values by copy, with no pointer or lifetime requirements.
    let (x, y) = unsafe {
        if edge == RIGHT || edge == LEFT {
            let lo = CGRectGetMinY(full);
            let hi = CGRectGetMaxY(full) - h;
            let y = clamp(
                CGRectGetMidY(full) - h / 2.0 - offset,
                lo - slack + trailing,
                hi + slack - leading,
            );
            let x = if edge == RIGHT {
                CGRectGetMaxX(usable) - w
            } else {
                CGRectGetMinX(usable)
            };
            if keep {
                (
                    clamp(x, CGRectGetMinX(full), CGRectGetMaxX(full) - w),
                    clamp(y, lo, hi),
                )
            } else {
                (x, y)
            }
        } else {
            let lo = CGRectGetMinX(full);
            let hi = CGRectGetMaxX(full) - w;
            let x = clamp(
                CGRectGetMidX(full) - w / 2.0 + offset,
                lo - slack + leading,
                hi + slack - trailing,
            );
            let y = if edge == TOP {
                CGRectGetMaxY(if hardware { full } else { usable }) - h
            } else {
                CGRectGetMinY(usable)
            };
            if keep {
                (
                    clamp(x, lo, hi),
                    clamp(y, CGRectGetMinY(full), CGRectGetMaxY(full) - h),
                )
            } else {
                (x, y)
            }
        }
    };
    Rect {
        origin: Point {
            x: x.round(),
            y: y.round(),
        },
        size: Size { w, h },
    }
}

// Resting targets are narrower than the panel and receive an intentional hot margin.
// Expanded targets cover only the drawn notch. Preserve native edge and scale ordering.
#[unsafe(no_mangle)]
pub extern "C" fn cr_window_body(
    panel: Size,
    edge: u32,
    slack: f64,
    shape_length: f64,
    scale: f64,
    depth: f64,
    rest_length: f64,
    hot_zone: f64,
    hot_margin: f64,
    folded: bool,
) -> Rect {
    let shape = shape_length * scale;
    let (along, length, depth) = if folded {
        let rest = rest_length * scale;
        let length = if rest <= hot_zone { hot_zone } else { rest };
        (
            slack + (shape - length) / 2.0,
            length,
            depth * scale + hot_margin,
        )
    } else {
        (slack, shape, depth * scale)
    };
    match edge {
        RIGHT => Rect {
            origin: Point {
                x: panel.w - depth,
                y: along,
            },
            size: Size {
                w: depth,
                h: length,
            },
        },
        LEFT => Rect {
            origin: Point { x: 0.0, y: along },
            size: Size {
                w: depth,
                h: length,
            },
        },
        TOP => Rect {
            origin: Point { x: along, y: 0.0 },
            size: Size {
                w: length,
                h: depth,
            },
        },
        _ => Rect {
            origin: Point {
                x: along,
                y: panel.h - depth,
            },
            size: Size {
                w: length,
                h: depth,
            },
        },
    }
}

#[repr(C)]
pub struct CursorHover {
    settings: bool,
    account: bool,
}

/// Enter through the settings handle. The gap between controls keeps a revealed
/// account switcher open, and an open account menu survives pointer movement.
#[unsafe(no_mangle)]
pub extern "C" fn cr_window_cursor_hover(
    expanded: bool,
    settings_were_visible: bool,
    menu_open: bool,
    over_settings: bool,
    over_account: bool,
    inside_controls: bool,
) -> CursorHover {
    let settings =
        expanded && (over_settings || menu_open || (settings_were_visible && inside_controls));
    CursorHover {
        settings,
        account: settings && over_account,
    }
}

#[derive(Default)]
#[repr(C)]
pub struct Handles {
    bounds: Rect,
    hits: bool,
}

/// # Safety
/// Nonempty points is an initialized, aligned array of count CGPoints lent for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_window_handles(
    points: *const Point,
    count: isize,
    panel: Size,
    edge: u32,
    slack: f64,
    scale: f64,
    diameter: f64,
    cursor: Point,
    hit_test: bool,
) -> Handles {
    let mut result = Handles::default();
    if count <= 0 || points.is_null() || count as usize > isize::MAX as usize / size_of::<Point>() {
        return result;
    }
    // SAFETY: caller provides aligned and initialized values, and bounds were checked above.
    let points = unsafe { slice::from_raw_parts(points, count as usize) };
    let side = diameter * scale;
    let (along, across) = match edge {
        RIGHT => (cursor.y, panel.w - cursor.x),
        LEFT => (cursor.y, cursor.x),
        TOP => (cursor.x, cursor.y),
        _ => (cursor.x, panel.h - cursor.y),
    };
    for (index, point) in points.iter().enumerate() {
        let a = slack + point.x * scale;
        let b = point.y * scale;
        let (x, y) = match edge {
            RIGHT => (panel.w - b, a),
            LEFT => (b, a),
            TOP => (a, b),
            _ => (a, panel.h - b),
        };
        let rect = Rect {
            origin: Point {
                x: x - side / 2.0,
                y: y - side / 2.0,
            },
            size: Size { w: side, h: side },
        };
        // SAFETY: CGRectUnion receives plain values and returns a plain value.
        result.bounds = if index == 0 {
            rect
        } else {
            unsafe { CGRectUnion(result.bounds, rect) }
        };
        if hit_test && !result.hits {
            result.hits = ((along - slack) / scale - point.x).hypot(across / scale - point.y)
                <= diameter / 2.0;
        }
    }
    result
}
