"""Build, validate and transactionally install the current-user Windows helper."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid

TASK = 'Discord Link Fixer'
LEGACY_RECOVERY = 'Discord Link Fixer Recovery'
SOURCES = ('DiscordLinkFixer.cs', 'ClipboardTransactions.cs', 'PasteService.cs', 'WindowsTests.cs')


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def preflight(backend, exe, update, require_stopped=True):
    tasks = {}
    for name in (TASK, LEGACY_RECOVERY):
        task = backend.get_task(name)
        if task is not None:
            require(update and backend.owns_task(task, exe), name + ' ownership check failed.')
        tasks[name] = task
    shortcut = backend.read_shortcut()
    if shortcut is not None:
        require(update and backend.owns_shortcut(shortcut, exe), 'Desktop shortcut ownership check failed.')
    if require_stopped:
        require(not backend.running(exe), 'Helper still running. Quit the verified helper first; no process was stopped.')
    return tasks, shortcut


def build(root, destination):
    compiler = Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe')
    require(compiler.is_file(), '.NET Framework compiler unavailable.')
    exe = destination / 'DiscordLinkFixer.exe'
    command = [str(compiler), '/nologo', '/target:winexe', '/optimize+',
               '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
               '/reference:System.Web.Extensions.dll', '/out:' + str(exe)]
    command.extend(str(root / name) for name in SOURCES)
    result = subprocess.run(command, capture_output=True, text=True, timeout=60,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    require(result.returncode == 0, 'Compilation failed: ' + result.stdout + result.stderr)
    fixture = root.parent / 'link-tests.json'
    shutil.copy2(fixture if fixture.exists() else root / 'link-tests.json', destination / 'link-tests.json')
    result = subprocess.run([str(exe), '--self-test'], capture_output=True, text=True, timeout=30,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    require(result.returncode == 0, 'Helper self-tests failed: ' + result.stdout + result.stderr)
    tests = json.loads((destination / 'self-test.json').read_text(encoding='utf-8'))
    require(tests.get('passed') is True, 'Helper did not report successful self-tests.')
    return exe, tests


def install(root, backend, update=False, check=False, builder=build):
    root = Path(root).resolve()
    exe = root / 'DiscordLinkFixer.exe'
    tasks, shortcut = preflight(backend, exe, update, not check)
    with tempfile.TemporaryDirectory(prefix='discord-link-fixer-build-') as directory:
        candidate, tests = builder(root, Path(directory))
        if check:
            return {'checked': True, 'installed': False, 'tests': tests}
        current_tasks, current_shortcut = preflight(backend, exe, update)
        require(tasks == current_tasks and shortcut == current_shortcut,
                'Installation artifacts changed during validation; nothing installed.')
        backup = Path(tempfile.mkdtemp(prefix='install-backup-', dir=str(root)))
        receipt_path = root / 'install-receipt.json'
        for path in (exe, receipt_path):
            if path.exists():
                shutil.copy2(path, backup / path.name)
        changed = []
        try:
            # Record each attempted mutation first, including APIs that fail after a partial write.
            changed.append('exe')
            shutil.copy2(candidate, exe)
            changed.append('task')
            backend.register_task(exe)
            if tasks[LEGACY_RECOVERY] is not None:
                changed.append('recovery')
                backend.delete_task(LEGACY_RECOVERY)
            changed.append('shortcut')
            backend.write_shortcut(exe)
            changed.append('started')
            backend.start_task(exe)
            state = backend.wait_ready(exe)
            receipt = {'tests': tests, 'running': state, 'path': str(exe), 'task': TASK,
                       'loginStart': True, 'supervisorRestart': True, 'periodicRecoveryTask': False,
                       'sourceHashes': {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in SOURCES},
                       'exeSha256': hashlib.sha256(exe.read_bytes()).hexdigest(), 'backup': str(backup)}
            receipt_path.write_text(json.dumps(receipt, indent=2), encoding='utf-8')
            return receipt
        except Exception as original_error:
            if 'started' in changed:
                backend.stop_started()
            rollback_errors = []
            for phase, undo in (
                    ('shortcut', lambda: backend.restore_shortcut(shortcut)),
                    ('recovery', lambda: backend.restore_task(LEGACY_RECOVERY, tasks[LEGACY_RECOVERY])),
                    ('task', lambda: backend.restore_task(TASK, tasks[TASK]))):
                if phase in changed:
                    try:
                        undo()
                    except Exception as error:
                        rollback_errors.append(phase + ':' + type(error).__name__)
            if 'exe' in changed:
                saved = backup / exe.name
                if saved.exists():
                    shutil.copy2(saved, exe)
                else:
                    exe.unlink(missing_ok=True)
            saved = backup / receipt_path.name
            if saved.exists():
                shutil.copy2(saved, receipt_path)
            else:
                receipt_path.unlink(missing_ok=True)
            if rollback_errors:
                raise RuntimeError('Rollback incomplete; preserved backup at ' + str(backup) + '. ' + ', '.join(rollback_errors)) from original_error
            raise


class WindowsBackend:
    def __init__(self):
        import psutil
        import win32api
        import win32com.client
        import win32security
        self.psutil, self.security = psutil, win32security
        self.account = win32api.GetUserNameEx(2)
        self.sid = win32security.ConvertSidToStringSid(win32security.LookupAccountName(None, self.account)[0])
        self.scheduler = win32com.client.Dispatch('Schedule.Service')
        self.scheduler.Connect()
        self.folder = self.scheduler.GetFolder('\\')
        self.shell = win32com.client.Dispatch('WScript.Shell')
        self.shortcut = Path(self.shell.SpecialFolders('Desktop')) / 'Discord Link Fixer.lnk'
        self.exe = None
        self.started_at = None
        self.token = None

    def get_task(self, name):
        try:
            task = self.folder.GetTask(name)
            return {'xml': task.Xml, 'enabled': bool(task.Enabled)}
        except Exception as error:
            info = getattr(error, 'excepinfo', None)
            code = info[5] if info and len(info) > 5 and info[5] else getattr(error, 'hresult', 0)
            if code & 0xffffffff == 0x80070002:
                return None
            raise  # Inaccessible is not absent.

    def owns_task(self, task, exe):
        import xml.etree.ElementTree as ET
        document = ET.fromstring(task['xml'])
        ns = {'t': 'http://schemas.microsoft.com/windows/2004/02/mit/task'}
        actions = document.findall('t:Actions/t:Exec', ns)
        all_actions = document.find('t:Actions', ns)
        if len(actions) != 1 or all_actions is None or len(all_actions) != 1:
            return False
        user = document.find('t:Principals/t:Principal/t:UserId', ns)
        level = document.find('t:Principals/t:Principal/t:RunLevel', ns)
        logon = document.find('t:Principals/t:Principal/t:LogonType', ns)
        try:
            owner = self.security.ConvertSidToStringSid(self.security.LookupAccountName(None, user.text)[0])
        except Exception:
            owner = user.text if user is not None else None
        command = actions[0].find('t:Command', ns)
        args = actions[0].find('t:Arguments', ns)
        directory = actions[0].find('t:WorkingDirectory', ns)
        return (owner == self.sid and command is not None and Path(command.text).resolve() == exe.resolve()
                and args is not None and args.text in ('--login', '--resume', '--recover', '--login $(Arg0)')
                and directory is not None and Path(directory.text).resolve() == exe.parent.resolve()
                and logon is not None and logon.text == 'InteractiveToken'
                and (level is None or level.text == 'LeastPrivilege'))

    def read_shortcut(self):
        return self.shortcut.read_bytes() if self.shortcut.exists() else None

    def owns_shortcut(self, shortcut, exe):
        link = self.shell.CreateShortCut(str(self.shortcut))
        return Path(link.Targetpath).resolve() == exe.resolve() and link.Arguments in ('--login', '--resume')

    def running(self, exe):
        for process in self.psutil.process_iter(['name']):
            if (process.info['name'] or '').lower() != exe.name.lower():
                continue
            try:
                if Path(process.exe()).resolve() == exe.resolve():
                    return True
            except self.psutil.NoSuchProcess:
                pass
        return False

    def register_task(self, exe):
        task = self.scheduler.NewTask(0)
        task.RegistrationInfo.Description = 'Discord link paste helper. Login and guarded supervisor recovery; no periodic duplicate launch.'
        task.Principal.UserId = self.account
        task.Principal.LogonType = 3
        task.Principal.RunLevel = 0
        task.Settings.Enabled = True
        task.Settings.AllowDemandStart = True
        task.Settings.DisallowStartIfOnBatteries = False
        task.Settings.StopIfGoingOnBatteries = False
        task.Settings.ExecutionTimeLimit = 'PT0S'
        task.Settings.MultipleInstances = 2
        task.Settings.RestartInterval = 'PT1M'
        task.Settings.RestartCount = 3
        task.Settings.StartWhenAvailable = True
        task.Settings.IdleSettings.StopOnIdleEnd = False
        trigger = task.Triggers.Create(9)
        trigger.UserId = self.account
        action = task.Actions.Create(0)
        action.Path, action.Arguments, action.WorkingDirectory = str(exe), '--login $(Arg0)', str(exe.parent)
        self.folder.RegisterTaskDefinition(TASK, task, 6, self.account, None, 3)

    def delete_task(self, name):
        self.folder.DeleteTask(name, 0)

    def restore_task(self, name, snapshot):
        if snapshot is None:
            if self.get_task(name) is not None:
                self.folder.DeleteTask(name, 0)
        else:
            task = self.folder.RegisterTask(name, snapshot['xml'], 6, self.account, None, 3)
            task.Enabled = snapshot['enabled']

    def write_shortcut(self, exe):
        link = self.shell.CreateShortCut(str(self.shortcut))
        link.Targetpath, link.Arguments, link.WorkingDirectory = str(exe), '--resume', str(exe.parent)
        link.Description = 'Discord link helper. Pause or Quit from its tray menu.'
        link.Save()

    def restore_shortcut(self, snapshot):
        if snapshot is None:
            self.shortcut.unlink(missing_ok=True)
        else:
            self.shortcut.write_bytes(snapshot)

    def start_task(self, exe):
        self.exe = exe
        self.started_at = time.time()
        self.token = '--install-token=' + uuid.uuid4().hex
        self.folder.GetTask(TASK).Run(self.token)  # Task Scheduler requires BSTR, not a VARIANT tuple.

    def wait_ready(self, exe):
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            try:
                state = json.loads((exe.parent / 'running.json').read_text(encoding='utf-8'))
                startup = json.loads((exe.parent / 'startup.json').read_text(encoding='utf-8'))
                process = self.psutil.Process(state['pid'])
                if (startup.get('pid') == state['pid'] and startup.get('phase') == 'ready'
                        and state.get('hookInstalled') and state.get('session', 0) > 0
                        and process.create_time() >= self.started_at - 1 and Path(process.exe()).resolve() == exe.resolve()):
                    return state
            except (OSError, ValueError, KeyError, self.psutil.NoSuchProcess, self.psutil.AccessDenied):
                pass
            time.sleep(0.25)
        raise RuntimeError('New worker readiness timed out; installation will be rolled back.')

    def stop_started(self):
        if self.exe is None or self.started_at is None:
            return
        owned = []
        for process in self.psutil.process_iter(['name']):
            if (process.info['name'] or '').lower() != self.exe.name.lower():
                continue
            try:
                if (Path(process.exe()).resolve() == self.exe.resolve()
                        and process.create_time() >= self.started_at - 1
                        and self.token in process.cmdline()):
                    owned.append(process)
            except self.psutil.NoSuchProcess:
                pass
        for process in owned:
            try:
                process.terminate()
            except self.psutil.NoSuchProcess:
                pass
        _, alive = self.psutil.wait_procs(owned, timeout=10)
        require(not alive, 'Transaction-owned worker did not stop; refusing to overwrite its binary.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--update-owned', action='store_true')
    parser.add_argument('--rebuild', action='store_true', help='Compatibility flag; builds are always source-verified.')
    parser.add_argument('--check', action='store_true', help='Validate without changing executable, shortcut or tasks.')
    args = parser.parse_args()
    print(json.dumps(install(Path(__file__).resolve().parent, WindowsBackend(), args.update_owned, args.check)))


if __name__ == '__main__':
    main()
