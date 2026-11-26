"""Exercise atomically replaced snapshots with redirected CLI output and real cancellation."""
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time


@contextmanager
def private_fixture():
    temporary = tempfile.TemporaryDirectory(prefix="coderim-standalone-cli-")
    try:
        yield Path(temporary.name)
    finally:
        # Windows can keep a single-file helper's image mapped briefly after exit.
        deadline = time.monotonic() + 5
        while True:
            try:
                temporary.cleanup()
                break
            except PermissionError as error:
                if getattr(error, "winerror", None) != 32 or time.monotonic() >= deadline:
                    raise
                time.sleep(0.1)


def compare_watch_frames(legacy, native, snapshot, env, interrupt):
    watched = snapshot.with_name("watch.json")
    replacement = snapshot.with_name("replacement")
    expected = {}
    for label, exe in (("legacy", legacy), ("rust", native)):
        watched.write_bytes(snapshot.read_bytes())
        changed = json.loads(watched.read_bytes())
        changed["providers"][0]["name"] = "Synthetic replaced frame"
        changed["extension"] = {"compatibility": True}
        replacement.write_bytes(b"\xfe\xff" + json.dumps(changed).encode("utf-16-be"))
        frames = [subprocess.check_output([exe, "tokens", "--snapshot", path], env=env, timeout=20)
                  for path in (watched, replacement)]
        assert frames[0] != frames[1]
        log = watched.with_name(label + ".watch")
        with log.open("wb") as stream, subprocess.Popen(
            [exe, "tokens", "--watch", "1", "--snapshot", watched], env=env,
            stdout=stream, stderr=subprocess.PIPE,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
        ) as child:
            try:
                for i in range(2):
                    if i == 1:
                        os.replace(replacement, watched)
                    wanted = b"".join(frames[:i + 1])
                    deadline = time.monotonic() + 10
                    while time.monotonic() < deadline and child.poll() is None:
                        if log.read_bytes() == wanted:
                            break
                        time.sleep(0.025)
                    else:
                        raise AssertionError((label, "missing atomic watch frame", i, child.poll()))
                if interrupt is None:
                    child.send_signal(signal.SIGINT)
                else:
                    interrupt(child)
                status = child.wait(timeout=8)
                assert status == 0, (label, "watch interrupt", status)
                expected[label] = {"frames": hashlib.sha256(wanted).hexdigest(), "interruptExit": status}
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait(timeout=5)
    assert expected["legacy"]["frames"] == expected["rust"]["frames"]
    return expected
