"""Compare this fixture's private Windows console buffer and targeted watch cancellation."""
import argparse
from contextlib import ExitStack
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

from watch_frames import compare_watch_frames


class Coord(ctypes.Structure):
    _fields_ = [('x', ctypes.c_short), ('y', ctypes.c_short)]


class CharInfo(ctypes.Structure):
    _fields_ = [('code_unit', ctypes.c_uint16), ('attributes', ctypes.c_uint16)]


class Rectangle(ctypes.Structure):
    _fields_ = [('left', ctypes.c_short), ('top', ctypes.c_short),
                ('right', ctypes.c_short), ('bottom', ctypes.c_short)]


def screen_text(raw):
    cells = (CharInfo * (len(raw) // ctypes.sizeof(CharInfo))).from_buffer_copy(raw)
    units = (ctypes.c_uint16 * len(cells))(*(cell.code_unit for cell in cells))
    return bytes(units).decode('utf-16-le', errors='surrogatepass')


class BufferInfo(ctypes.Structure):
    _fields_ = [('size', Coord), ('cursor', Coord), ('attributes', wintypes.WORD),
                ('window', ctypes.c_short * 4), ('maximum', Coord)]


def run(legacy, native, snapshot, result):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)

    def api(name, arguments, returns=wintypes.BOOL):
        f = getattr(kernel, name)
        f.argtypes = arguments
        f.restype = returns
        return f

    def checked(value):
        if not value:
            raise ctypes.WinError(ctypes.get_last_error())
        return value

    dword = wintypes.DWORD
    number = ctypes.POINTER(dword)
    handle = wintypes.HANDLE
    processes = api('GetConsoleProcessList', [number, dword], dword)
    # Never modify a console shared with a shell or CI agent.
    pids = (dword * 16)()
    attached = processes(pids, len(pids))
    if attached != 1 or pids[0] != os.getpid():
        raise RuntimeError(f'Private test console ownership could not be established ({attached} attached).')
    set_page = api('SetConsoleOutputCP', [wintypes.UINT])
    cursor = api('SetConsoleCursorPosition', [handle, Coord])
    fill = api('FillConsoleOutputCharacterW', [handle, wintypes.WCHAR, dword, Coord, number])
    fill_attributes = api('FillConsoleOutputAttribute', [handle, wintypes.WORD, dword, Coord, number])
    read = api('ReadConsoleOutputW', [handle, ctypes.POINTER(CharInfo), Coord, Coord,
                                     ctypes.POINTER(Rectangle)])
    dimensions = api('GetConsoleScreenBufferInfo', [handle, ctypes.POINTER(BufferInfo)])
    send = api('GenerateConsoleCtrlEvent', [dword, dword])
    import msvcrt
    with ExitStack() as resources:
        output = os.open('CONOUT$', os.O_RDWR | os.O_BINARY)
        resources.callback(os.close, output)
        input_ = os.open('CONIN$', os.O_RDONLY | os.O_BINARY)
        resources.callback(os.close, input_)
        out = handle(msvcrt.get_osfhandle(output))
        info = BufferInfo()
        checked(dimensions(out, ctypes.byref(info)))
        width, height = info.size.x, min(8, info.size.y)
        size = width * height
        def clear():
            count = dword()
            checked(fill(out, ' ', size, Coord(0, 0), ctypes.byref(count)))
            assert count.value == size
            checked(fill_attributes(out, info.attributes, size, Coord(0, 0), ctypes.byref(count)))
            assert count.value == size
            checked(cursor(out, Coord(0, 0)))
        def cells():
            # Wide glyphs can occupy multiple screen cells. Read both the UTF-16
            # unit and its attributes (including leading/trailing cell flags).
            region = Rectangle(0, 0, width - 1, height - 1)
            buf = (CharInfo * size)()
            checked(read(out, buf, Coord(width, height), Coord(0, 0), ctypes.byref(region)))
            actual = (region.left, region.top, region.right, region.bottom)
            if actual != (0, 0, width - 1, height - 1):
                raise RuntimeError(f'Expected {width} by {height} console cells, received {actual}')
            return bytes(buf)
        rows = {}
        for page in (437, 949, 65001):
            checked(set_page(page))
            for label, arguments, expected in [
                ('path', ['path', '--snapshot', 'Café 한글 e\u0301 😀 — fullwidth Ｚ'], 0),
                ('error', ['--알수없는😀'], 1),
                ('usage', ['usage', '--snapshot', snapshot], 0),
            ]:
                pair = []
                for exe in (legacy, native):
                    clear()
                    completed = subprocess.run([exe, *arguments], stdin=input_, stdout=output,
                                               stderr=output, timeout=20)
                    assert completed.returncode == expected
                    raw = cells()
                    assert screen_text(raw).strip()
                    pair.append(raw)
                if pair[0] != pair[1]:
                    index = next(i for i in range(len(pair[0])) if pair[0][i] != pair[1][i])
                    raise ValueError((page, label, index, pair[0][max(0,index-12):index+20].hex(),
                                      pair[1][max(0,index-12):index+20].hex()))
                rows[f'{page}-{label}'] = hashlib.sha256(pair[0]).hexdigest()
        # Generate a control event for this fixture's still-live child group only.
        status = {}
        for label, exe in [('legacy', legacy), ('rust', native)]:
            clear()
            child = subprocess.Popen([exe, 'tokens', '--watch', '3600', '--snapshot', snapshot],
                                     stdin=input_, stdout=output, stderr=output,
                                     creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
            try:
                end = time.monotonic() + 12
                while time.monotonic() < end and child.poll() is None:
                    if 'tokens' in screen_text(cells()):
                        break
                    time.sleep(.025)
                else:
                    raise TimeoutError(label + ' did not show an initial frame')
                checked(send(1, child.pid))  # CTRL_BREAK_EVENT
                status[label] = child.wait(timeout=8)
                assert status[label] == 0
            finally:
                if child.poll() is None:
                    child.kill()
                child.wait()
        def interrupt_redirected(child):
            # The compatibility host can still be flushing the redirected second frame.
            # Let it finish before delivering Ctrl+Break to the live CLI's own group.
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline and child.poll() is None:
                count = processes(pids, len(pids))
                if count == 2 and set(pids[:count]) == {os.getpid(), child.pid}:
                    checked(send(1, child.pid))
                    return
                time.sleep(0.025)
            raise RuntimeError('The watch process did not return from its compatibility reader')

        redirected = compare_watch_frames(legacy, native, snapshot, os.environ.copy(), interrupt_redirected)
        result.write_text(json.dumps({'screenSha256': rows, 'watchBreakExit': status,
                                      'redirectedWatch': redirected}), encoding='utf8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for arg in ['legacy', 'native', 'snapshot', 'result']:
        parser.add_argument(arg, type=Path)
    args = parser.parse_args()
    run(args.legacy, args.native, args.snapshot, args.result)
