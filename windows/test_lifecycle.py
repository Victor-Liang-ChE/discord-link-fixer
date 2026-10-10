import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid
import argparse
import os
import psutil
import win32process

ctypes.windll.kernel32.SetErrorMode(0x8003)
user32 = ctypes.WinDLL('user32', use_last_error=True)
user32.CreateWindowStationW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
user32.CreateWindowStationW.restype = wintypes.HANDLE
user32.GetProcessWindowStation.restype = wintypes.HANDLE
user32.SetProcessWindowStation.argtypes = [wintypes.HANDLE]
user32.CreateDesktopW.argtypes = [wintypes.LPCWSTR, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
user32.CreateDesktopW.restype = wintypes.HANDLE
user32.CloseDesktop.argtypes = [wintypes.HANDLE]
user32.CloseWindowStation.argtypes = [wintypes.HANDLE]
parser = argparse.ArgumentParser(description='Isolated supervisor lifecycle gate. Does not stop an installed helper.')
parser.add_argument('executable', type=Path, help='Verified candidate DiscordLinkFixer.exe')
source = parser.parse_args().executable.resolve()
if not source.is_file():
    raise RuntimeError('Candidate executable does not exist.')
caller_session = wintypes.DWORD()
ctypes.windll.kernel32.ProcessIdToSessionId(os.getpid(), ctypes.byref(caller_session))
for process in psutil.process_iter(['name']):
    if (process.info['name'] or '').lower() == 'discordlinkfixer.exe':
        session = wintypes.DWORD()
        if ctypes.windll.kernel32.ProcessIdToSessionId(process.pid, ctypes.byref(session)) and session.value == caller_session.value:
            raise RuntimeError('A helper already owns this session. Run lifecycle tests in a separate session; no process was stopped.')
root = Path(tempfile.mkdtemp(prefix='discord-link-fixer-lifecycle-'))
exe = root / 'DiscordLinkFixer.exe'
shutil.copy2(source, exe)
name = 'DiscordLinkFixerLifecycle' + uuid.uuid4().hex
station = user32.CreateWindowStationW(name, 0, 0x37f, None)
if not station:
    raise ctypes.WinError(ctypes.get_last_error())
old_station = user32.GetProcessWindowStation()
desktop = None
process_handle = thread_handle = None
report = {'userClipboardTouched': False, 'aspenStopped': False, 'desktopSwitched': False, 'exeSha256': hashlib.sha256(exe.read_bytes()).hexdigest()}

def wait_ready(previous=0):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            startup = json.loads((root / 'startup.json').read_text(encoding='utf-8'))
            state = json.loads((root / 'running.json').read_text(encoding='utf-8'))
            pid = state['pid']
            process = psutil.Process(pid)
            if pid != previous and startup.get('pid') == pid and startup.get('phase') == 'ready' and Path(process.exe()).resolve() == exe.resolve():
                if state['session'] != caller_session.value:
                    raise RuntimeError('Lifecycle worker did not remain in the isolated caller session.')
                return state
        except (OSError, ValueError, KeyError, psutil.NoSuchProcess):
            pass
        time.sleep(0.1)
    raise RuntimeError('Isolated lifecycle worker readiness timed out.')

def matching_workers():
    matching = []
    for process in psutil.process_iter(['name']):
        if (process.info['name'] or '').lower() != exe.name.lower():
            continue
        try:
            if Path(process.exe()).resolve() == exe.resolve():
                matching.append(process)
        except psutil.NoSuchProcess:
            pass
    return matching

try:
    if not user32.SetProcessWindowStation(station):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        desktop = user32.CreateDesktopW('Default', None, None, 0, 0x1ff, None)
        if not desktop:
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        user32.SetProcessWindowStation(old_station)
    startup = win32process.STARTUPINFO()
    startup.lpDesktop = name + '\\Default'
    process_handle, thread_handle, parent_pid, thread_id = win32process.CreateProcess(
        str(exe), '"' + str(exe) + '" --login --install-token=' + uuid.uuid4().hex,
        None, None, False, subprocess.CREATE_NO_WINDOW, None, str(root), startup)
    first = wait_ready()
    report['initialSession'] = first['session']
    result = subprocess.run([str(exe), '--worker'], capture_output=True, text=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode != 21 or (root / 'quit-until-login').exists():
        raise RuntimeError('Duplicate instance was not rejected without an intentional-quit marker.')
    report['duplicateExit'] = result.returncode
    worker = psutil.Process(first['pid'])
    if Path(worker.exe()).resolve() != exe.resolve():
        raise RuntimeError('Isolated worker identity changed.')
    started = time.monotonic()
    worker.terminate()  # Only the new isolated test worker. Its private clipboard is empty.
    worker.wait(timeout=10)
    recovered = wait_ready(first['pid'])
    report['crashRecoverySeconds'] = round(time.monotonic() - started, 3)
    report['crashRecovered'] = True
    result = subprocess.run([str(exe), '--quit'], capture_output=True, text=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode != 0:
        raise RuntimeError('Isolated graceful-quit request failed.')
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        matching = matching_workers()
        if not matching:
            break
        time.sleep(0.1)
    else:
        raise RuntimeError('Isolated graceful quit did not stop the test helper.')
    report['intentionalQuitMarker'] = (root / 'quit-until-login').exists()
    if not report['intentionalQuitMarker']:
        raise RuntimeError('Intentional quit was not recorded.')
    result = subprocess.run([str(exe), '--recover'], capture_output=True, text=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode != 0:
        raise RuntimeError('Intentional quit not respected by legacy recovery command.')
    report['quitRespected'] = True
    report['passed'] = True
    (root / 'lifecycle.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
finally:
    for process in matching_workers():
        try:
            process.terminate()
            process.wait(timeout=10)
        except psutil.NoSuchProcess:
            pass
    if thread_handle:
        thread_handle.Close()
    if process_handle:
        process_handle.Close()
    if desktop:
        user32.CloseDesktop(desktop)
    user32.CloseWindowStation(station)
