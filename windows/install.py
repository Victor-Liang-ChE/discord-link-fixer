"""Compile and register the GUI helper. Run via SSH; never open a console window."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import win32com.client
import win32api
import datetime
import sys
import psutil

root = Path(__file__).resolve().parent
csc = Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe')
exe = root / 'DiscordLinkFixer.exe'
if not exe.exists() or '--rebuild' in sys.argv:
    for process in psutil.process_iter(['exe']):
        if process.info['exe'] and Path(process.info['exe']).resolve() == exe.resolve():
            raise RuntimeError('Helper still running; do not overwrite its executable.')
    result = subprocess.run([str(csc), '/nologo', '/target:winexe', '/optimize+',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll',
        '/out:' + str(exe), str(root / 'DiscordLinkFixer.cs')], capture_output=True, text=True,
        creationflags=subprocess.CREATE_NO_WINDOW, timeout=60)
    assert result.returncode == 0, result.stdout + result.stderr
result = subprocess.run([str(exe), '--self-test'], creationflags=subprocess.CREATE_NO_WINDOW, timeout=20)
assert result.returncode == 0
tests = json.loads((root / 'self-test.json').read_text())
assert tests['passed']

shell = win32com.client.Dispatch('WScript.Shell')
shortcut = shell.CreateShortCut(str(Path(shell.SpecialFolders('Desktop')) / 'Discord Link Fixer.lnk'))
shortcut.Targetpath = str(exe)
shortcut.Arguments = '--login'
shortcut.WorkingDirectory = str(root)
shortcut.Description = 'Convert X tweet links on Ctrl+V in Discord. Tray menu offers Pause and Quit.'
shortcut.Save()

scheduler = win32com.client.Dispatch('Schedule.Service')
scheduler.Connect()
folder = scheduler.GetFolder('\\')
name = 'Discord Link Fixer'
try:
    folder.GetTask(name)
except Exception:
    pass
else:
    prior = folder.GetTask(name)
    assert '--update-owned' in sys.argv and Path(prior.Definition.Actions.Item(1).Path).resolve() == exe.resolve(), 'Task already exists; inspect before replacing it.'
task = scheduler.NewTask(0)
task.RegistrationInfo.Description = 'User-requested Discord link paste helper. Starts at login, retries crashes, normal Quit stays stopped until next login.'
account = win32api.GetUserNameEx(2)
task.Principal.UserId = account
task.Principal.LogonType = 3  # InteractiveToken: the actual signed-in desktop, never SSH session 0.
task.Principal.RunLevel = 0
task.Settings.Enabled = True
task.Settings.AllowDemandStart = True
task.Settings.DisallowStartIfOnBatteries = False
task.Settings.StopIfGoingOnBatteries = False
task.Settings.ExecutionTimeLimit = 'PT0S'
task.Settings.MultipleInstances = 2  # IgnoreNew, plus an app-level per-session mutex.
task.Settings.RestartInterval = 'PT1M'
task.Settings.RestartCount = 3
task.Settings.StartWhenAvailable = True
task.Settings.IdleSettings.StopOnIdleEnd = False
trigger = task.Triggers.Create(9)  # AtLogon
trigger.UserId = account
action = task.Actions.Create(0)
action.Path = str(exe)
action.Arguments = '--login'
action.WorkingDirectory = str(root)
registered = folder.RegisterTaskDefinition(name, task, 6 if '--update-owned' in sys.argv else 2, account, None, 3)
recovery = scheduler.NewTask(0)
recovery.RegistrationInfo.Description = 'Discord Link Fixer recovery. Checks once a minute; per-session mutex avoids duplicates, and intentional Quit is respected until login.'
recovery.Principal.UserId = account
recovery.Principal.LogonType = 3
recovery.Principal.RunLevel = 0
recovery.Settings.Enabled = True
recovery.Settings.AllowDemandStart = True
recovery.Settings.DisallowStartIfOnBatteries = False
recovery.Settings.StopIfGoingOnBatteries = False
recovery.Settings.ExecutionTimeLimit = 'PT0S'
recovery.Settings.MultipleInstances = 2
recovery.Settings.StartWhenAvailable = True
recovery.Settings.IdleSettings.StopOnIdleEnd = False
recovery_trigger = recovery.Triggers.Create(1)
recovery_trigger.StartBoundary = (datetime.datetime.now() + datetime.timedelta(seconds=30)).isoformat(timespec='seconds')
recovery_trigger.Repetition.Interval = 'PT1M'
recovery_action = recovery.Actions.Create(0)
recovery_action.Path = str(exe)
recovery_action.Arguments = '--recover'
recovery_action.WorkingDirectory = str(root)
try:
    prior = folder.GetTask('Discord Link Fixer Recovery')
except Exception:
    pass
else:
    assert '--update-owned' in sys.argv and Path(prior.Definition.Actions.Item(1).Path).resolve() == exe.resolve()
folder.RegisterTaskDefinition('Discord Link Fixer Recovery', recovery, 6 if '--update-owned' in sys.argv else 2, account, None, 3)
registered.Run('')
for attempt in range(40):
    if (root / 'running.json').exists():
        state = json.loads((root / 'running.json').read_text())
        if state.get('hookInstalled') and psutil.pid_exists(state.get('pid', 0)) and Path(psutil.Process(state['pid']).exe()).resolve() == exe.resolve():
            break
    time.sleep(0.25)
else:
    raise RuntimeError('Task registered, but GUI helper did not report an installed hook. Check interactive login.')
receipt = {'tests': tests, 'running': state, 'path': str(exe), 'task': name,
    'loginStart': True, 'crashRetries': 3, 'hookRefreshSeconds': 60,
    'supervisorRestart': True, 'recoveryTask': 'Discord Link Fixer Recovery', 'recoveryIntervalSeconds': 60,
    'exeSha256': hashlib.sha256(exe.read_bytes()).hexdigest()}
(root / 'install-receipt.json').write_text(json.dumps(receipt, indent=2))
print(json.dumps(receipt))
