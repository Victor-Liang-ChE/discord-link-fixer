"""Run Windows gates without installing, starting Discord or accessing the user's clipboard."""
import ctypes
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid


def scheduler_gate(installer, destination, exe):
    import win32api
    import win32security
    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
    sid = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
    # An elevated shell can give TemporaryDirectory an Administrators owner. Only grant
    # this account read/execute on artifacts created by this verification, not user folders.
    for path in (destination, exe):
        descriptor = win32security.GetFileSecurity(str(path), win32security.DACL_SECURITY_INFORMATION)
        acl = descriptor.GetSecurityDescriptorDacl()
        flags = win32security.OBJECT_INHERIT_ACE | win32security.CONTAINER_INHERIT_ACE if path.is_dir() else 0
        acl.AddAccessAllowedAceEx(win32security.ACL_REVISION_DS, flags, 0x1200a9, sid)
        win32security.SetNamedSecurityInfo(str(path), win32security.SE_FILE_OBJECT, win32security.DACL_SECURITY_INFORMATION, None, None, acl, None)
    backend = installer.WindowsBackend()
    name = 'Discord Link Fixer Validation ' + uuid.uuid4().hex
    task = backend.scheduler.NewTask(0)
    task.RegistrationInfo.Description = 'Temporary bootstrap/parameter validation. No clipboard or Discord access.'
    task.Principal.UserId = backend.account
    task.Principal.LogonType = 3
    task.Principal.RunLevel = 0
    task.Settings.Enabled = True
    task.Settings.AllowDemandStart = True
    task.Settings.DisallowStartIfOnBatteries = False
    task.Settings.StopIfGoingOnBatteries = False
    task.Settings.ExecutionTimeLimit = 'PT30S'
    action = task.Actions.Create(0)
    action.Path, action.Arguments, action.WorkingDirectory = str(exe), '--bootstrap-check $(Arg0)', str(destination)
    registered = backend.folder.RegisterTaskDefinition(name, task, 2, backend.account, None, 3)
    try:
        registered.Run('--install-token=' + uuid.uuid4().hex)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            current = backend.folder.GetTask(name)
            if current.LastRunTime.year > 2000 and len(current.GetInstances(0)) == 0:
                break
            time.sleep(0.1)
        installer.require(current.LastTaskResult == 0, 'Native Task Scheduler parameter gate failed.')
        return {'exitCode': current.LastTaskResult, 'scalarBstrToken': True, 'taskRemoved': True}
    finally:
        current = backend.folder.GetTask(name)
        installer.require(Path(current.Definition.Actions.Item(1).Path).resolve() == exe.resolve(), 'Validation task identity changed.')
        if len(current.GetInstances(0)):
            current.Stop(0)  # Only this newly created, agent-owned bootstrap validation.
        backend.folder.DeleteTask(name, 0)


def run(command, expected=0):
    result = subprocess.run(command, capture_output=True, text=True, timeout=60,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode != expected:
        raise RuntimeError(f'Gate failed: {command[0]} code={result.returncode}\n{result.stdout}{result.stderr}')
    return {'exitCode': result.returncode, 'output': result.stdout + result.stderr}


def main():
    root = Path(__file__).resolve().parent
    require_windows = sys.platform == 'win32'
    if require_windows:
        ctypes.windll.kernel32.SetErrorMode(0x8003)
    report = {
        'installer': run([sys.executable, str(root / 'test_install.py'), '-v']),
        'installerOptimized': run([sys.executable, '-O', str(root / 'test_install.py'), '-v']),
        'userClipboardTouched': False, 'aspenStopped': False,
    }
    if not require_windows:
        report['windowsNativeGates'] = 'Not run. This command needs Windows for native verification.'
        print(json.dumps(report, indent=2))
        return
    spec = importlib.util.spec_from_file_location('installer', root / 'install.py')
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    with tempfile.TemporaryDirectory(prefix='discord-link-fixer-verification-') as directory:
        destination = Path(directory)
        exe, tests = installer.build(root, destination)
        report['pureTests'] = tests
        report['bootstrap'] = run([str(exe), '--bootstrap-check'])
        report['scheduler'] = scheduler_gate(installer, destination, exe)
        broken = destination / 'missing-gui'
        broken.mkdir()
        broken_exe = broken / exe.name
        shutil.copy2(exe, broken_exe)
        broken_exe.with_suffix('.exe.config').write_text(
            '<configuration><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">'
            '<dependentAssembly><assemblyIdentity name="System.Windows.Forms" publicKeyToken="b77a5c561934e089" culture="neutral"/>'
            '<publisherPolicy apply="no"/><bindingRedirect oldVersion="0.0.0.0-9.9.9.9" newVersion="9.9.9.9"/>'
            '</dependentAssembly></assemblyBinding></runtime></configuration>', encoding='utf-8')
        report['missingGuiBootstrap'] = run([str(broken_exe), '--bootstrap-check'])
        report['missingGuiDependency'] = run([str(broken_exe), '--dependency-check'], expected=1)
        error = (broken / 'last-error.txt').read_text(encoding='utf-8')
        if 'HRESULT=' not in error or 'FileNotFoundException' not in error:
            raise RuntimeError('Missing GUI dependency was not reported safely.')
        native = destination / 'NativeClipboardTests.exe'
        command = ['C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe', '/nologo', '/target:exe', '/main:NativeClipboardTests',
                   '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll',
                   '/out:' + str(native)]
        command.extend(str(root / name) for name in installer.SOURCES + ('NativeClipboardTests.cs',))
        report['nativeCompile'] = run(command)
        report['nativeClipboard'] = run([str(native)])
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
