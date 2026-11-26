//! Canonical right-edge notch commands and the affine transform to its screen edge.
//! Layout dimensions come from the app's existing NotchLayout design tokens.
#[repr(u32)]
#[derive(Default, Clone, Copy)]
enum Verb {
    #[default]
    Move,
    Line,
    ArcCw,
    ArcCcw,
    Close,
}

#[repr(C)]
#[derive(Default, Clone, Copy)]
struct Command {
    x: f64,
    y: f64,
    radius: f64,
    start_degrees: i16,
    end_degrees: i16,
    verb: Verb,
}

impl Command {
    fn point(verb: Verb, x: f64, y: f64) -> Self {
        Self {
            x,
            y,
            verb,
            ..Self::default()
        }
    }

    fn arc(x: f64, y: f64, radius: f64, start: i16, end: i16, verb: Verb) -> Self {
        Self {
            x,
            y,
            radius,
            start_degrees: start,
            end_degrees: end,
            verb,
        }
    }
}

#[repr(C)]
#[derive(Default)]
pub struct NotchPath {
    transform: [f64; 6],
    commands: [Command; 9],
    count: u32,
}

impl NotchPath {
    fn push(&mut self, command: Command) {
        self.commands[self.count as usize] = command;
        self.count += 1;
    }
}

#[repr(C)]
pub struct Parameters {
    edge: u32,
    has_hardware: u32,
    width: f64,
    height: f64,
    curl_radius: f64,
    corner_radius: f64,
    bezel_fillet: f64,
    hardware_height: f64,
}

// SAFETY: coderim_notch_path.h reserves this symbol and its by-value C layouts.
#[unsafe(no_mangle)]
pub extern "C" fn cr_make_notch_path(parameters: Parameters) -> NotchPath {
    let Parameters {
        edge,
        has_hardware,
        width,
        height,
        curl_radius,
        corner_radius,
        bezel_fillet,
        hardware_height,
    } = parameters;
    if ![
        width,
        height,
        curl_radius,
        corner_radius,
        bezel_fillet,
        hardware_height,
    ]
    .into_iter()
    .all(f64::is_finite)
        || width < 0.0
        || height < 0.0
    {
        return NotchPath::default();
    }
    let (depth, length) = if edge < 2 {
        (width, height)
    } else {
        (height, width)
    };
    // CGAffineTransform uses (a, b, c, d, tx, ty). Keep the native renderer's
    // two successive transformations: this orientation, then the rect's origin.
    let transform = match edge {
        0 => [1., 0., 0., 1., 0., 0.],
        1 => [-1., 0., 0., 1., depth, 0.],
        2 => [0., -1., 1., 0., 0., depth],
        3 => [0., 1., 1., 0., 0., 0.],
        _ => return NotchPath::default(),
    };
    let (flare, cap) = if has_hardware != 0 {
        (bezel_fillet, hardware_height / 2.)
    } else {
        (curl_radius, f64::MAX)
    };
    // Claim the outer corner first, preserving it as the resting pill narrows.
    let wanted = corner_radius.min(cap).min(depth / 2.).max(0.);
    let curl = flare.min(length / 2.).min(depth - wanted).max(0.);
    let corner = wanted.min((length - 2. * curl) / 2.).max(0.);
    let bottom = length - curl;

    let mut path = NotchPath {
        transform,
        ..NotchPath::default()
    };
    path.push(Command::point(Verb::Move, depth, 0.));
    if curl > 0. {
        path.push(Command::arc(depth - curl, 0., curl, 0, 90, Verb::ArcCcw));
    }
    path.push(Command::point(Verb::Line, corner, curl));
    path.push(Command::arc(
        corner,
        curl + corner,
        corner,
        270,
        180,
        Verb::ArcCw,
    ));
    path.push(Command::point(Verb::Line, 0., bottom - corner));
    path.push(Command::arc(
        corner,
        bottom - corner,
        corner,
        180,
        90,
        Verb::ArcCw,
    ));
    path.push(Command::point(Verb::Line, depth - curl, bottom));
    if curl > 0. {
        path.push(Command::arc(
            depth - curl,
            length,
            curl,
            270,
            360,
            Verb::ArcCcw,
        ));
    }
    path.push(Command::point(Verb::Close, 0., 0.));
    path
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parameters(edge: u32) -> Parameters {
        Parameters {
            edge,
            has_hardware: 0,
            width: 10.,
            height: 50.,
            curl_radius: 20.,
            corner_radius: 8.,
            bezel_fillet: 1.5,
            hardware_height: 4.,
        }
    }

    #[test]
    fn collapsed_pill_reserves_corner_before_flare() {
        let path = cr_make_notch_path(parameters(0));
        assert_eq!(path.count, 9);
        assert_eq!(path.commands[1].radius, 5.);
        assert_eq!(path.commands[3].radius, 5.);

        let joined = cr_make_notch_path(Parameters {
            has_hardware: 1,
            ..parameters(1)
        });
        assert_eq!(joined.transform, [-1., 0., 0., 1., 10., 0.]);
        assert_eq!(joined.commands[1].radius, 1.5);
        assert_eq!(joined.commands[3].radius, 2.);
    }

    #[test]
    fn square_edges_and_fallback_have_bounded_command_counts() {
        let path = cr_make_notch_path(Parameters {
            curl_radius: 0.,
            ..parameters(2)
        });
        assert_eq!(path.count, 7);
        assert!(matches!(path.commands[6].verb, Verb::Close));
        assert_eq!(cr_make_notch_path(parameters(4)).count, 0);
        assert_eq!(
            cr_make_notch_path(Parameters {
                width: f64::INFINITY,
                ..parameters(0)
            })
            .count,
            0
        );
    }
}
