"""Decode the complete images and bound differences against repeated animation captures."""
from pathlib import Path
import subprocess


def decode(decoder, image, raw_file):
    size = tuple(map(int, subprocess.check_output([decoder, image, raw_file], text=True).split()))
    pixels = raw_file.read_bytes()
    count = size[0] * size[1]
    if len(pixels) != count * 4 or not any(pixels[3::4]) or pixels == pixels[:4] * count:
        raise ValueError(f"Empty or truncated image: {image.name}")
    return size, pixels


def difference(decoder, first, second, raw_file):
    size, original = decode(decoder, first, raw_file)
    other_size, candidate = decode(decoder, second, raw_file)
    if size != other_size:
        raise ValueError(f"{first.name} dimensions differ: {size} and {other_size}")
    count = 0
    xmin, ymin = size
    xmax = ymax = -1
    if original != candidate:
        for offset in range(0, len(original), 4):
            if original[offset:offset + 4] == candidate[offset:offset + 4]:
                continue
            y, x = divmod(offset // 4, size[0])
            xmin, ymin = min(xmin, x), min(ymin, y)
            xmax, ymax = max(xmax, x), max(ymax, y)
            count += 1
    return {"size": size, "changedPixels": count,
            "bounds": [xmin, ymin, xmax, ymax] if count else None}


def inside(bounds, limit):
    return not bounds or (limit is not None and limit[0] <= bounds[0] <= bounds[2] <= limit[2]
                          and limit[1] <= bounds[1] <= bounds[3] <= limit[3])


def summarize(decoder, reference, candidate, scratch):
    raw = scratch / "decoded.rgba"
    notch = ["notch-readings", "notch-codex-card", "notch-codex-idle-card", "notch-claude-card"]
    menus = [name + "-" + theme for name in ["Overview", "Usage", "Projects", "Sessions"]
             for theme in ["light", "dark"]]
    settings = ["accounts-settings-light", "accounts-settings-dark"]
    states = {}
    for name in [*notch, *menus, *settings]:
        image = name + ".png"
        states[name] = difference(decoder, reference / image, candidate / image, raw)
    repeats = {}
    for mode, directory in [("reference", reference), ("candidate", candidate)]:
        repeats[mode] = {name: difference(decoder, directory / (name + ".png"),
                                          directory / "repeat" / (name + ".png"), raw) for name in notch}
    repeats["paired"] = {name: difference(decoder, reference / "repeat" / (name + ".png"),
                                          candidate / "repeat" / (name + ".png"), raw) for name in notch}
    actions = {}
    for mode, directory in [("reference", reference), ("candidate", candidate)]:
        actions[mode] = [difference(decoder, directory / "notch-readings.png",
                                    directory / "notch-codex-card.png", raw)["changedPixels"],
                         difference(decoder, directory / "notch-codex-card.png",
                                    directory / "notch-claude-card.png", raw)["changedPixels"]]
    busy = "notch-codex-card"
    # Use the variation within each unchanged binary to locate the spinner in this
    # renderer. Its 17px ring at 2x may shift when a different SDK lays out text.
    jitter = [repeats[mode][busy]["bounds"] for mode in ("reference", "candidate")]
    bounds = [b for b in jitter if b]
    limit = ([min(b[0] for b in bounds), min(b[1] for b in bounds),
              max(b[2] for b in bounds), max(b[3] for b in bounds)] if bounds else None)
    static_failures = [name for name, diff in states.items() if name != busy and diff["changedPixels"]]
    for label, pair in repeats.items():
        static_failures += [f"{label}/{name}" for name, diff in pair.items() if name != busy and diff["changedPixels"]]
    animation_ok = (not limit or (limit[2] - limit[0] < 24 and limit[3] - limit[1] < 24))
    animation_ok = animation_ok and all(inside(row["bounds"], limit)
                                        for row in (states[busy], repeats["paired"][busy]))
    return {"states": states, "notchRepeats": repeats, "hoverChangedPixels": actions,
            "spinnerControlBounds": limit, "allStaticMatch": not static_failures,
            "staticFailures": static_failures, "animatedDeltaWithinControl": animation_ok,
            "hoverChangesVisible": all(all(x > 0 for x in row) for row in actions.values())}
