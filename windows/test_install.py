"""Installer fault injection. No Windows tasks, user clipboard or real processes are touched."""
import copy
import importlib.util
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

spec = importlib.util.spec_from_file_location('installer', Path(__file__).with_name('install.py'))
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class Backend:
    def __init__(self):
        self.tasks = {installer.TASK: {'xml': 'owned-primary', 'enabled': False},
                      installer.LEGACY_RECOVERY: {'xml': 'owned-recovery', 'enabled': True}}
        self.shortcut = b'owned shortcut'
        self.startup = Path('Startup') / 'Discord Link Fixer.lnk'
        self.startup_data = None
        self.live = False
        self.calls = []
        self.fail = None

    def get_task(self, name):
        return copy.deepcopy(self.tasks.get(name))

    def owns_task(self, task, exe):
        return task['xml'].startswith('owned-')

    def read_shortcut(self):
        return self.shortcut

    def owns_shortcut(self, shortcut, exe):
        return shortcut.startswith(b'owned')

    def read_startup(self):
        return self.startup_data

    def owns_startup(self, snapshot, exe):
        return snapshot.startswith(b'owned')

    def write_startup(self, exe):
        self.startup_data = b'owned startup'
        self.mutate('startup')

    def restore_startup(self, snapshot):
        self.startup_data = snapshot
        self.calls.append('undo startup')

    def running(self, exe):
        return self.live

    def mutate(self, label):
        self.calls.append(label)
        if self.fail == label:
            raise RuntimeError('Injected failure after ' + label)

    def register_task(self, exe):
        self.tasks[installer.TASK] = {'xml': 'owned-new', 'enabled': True}
        self.mutate('task')

    def delete_task(self, name):
        del self.tasks[name]
        self.mutate('recovery')

    def write_shortcut(self, exe):
        self.shortcut = b'owned new shortcut'
        self.mutate('shortcut')

    def start_task(self, exe):
        self.live = True
        self.mutate('started')

    def wait_ready(self, exe):
        self.mutate('ready')
        return {'pid': 123, 'session': 1, 'hookInstalled': True}

    def stop_started(self):
        self.calls.append('stop transaction-owned')
        self.live = False

    def restore_shortcut(self, snapshot):
        self.shortcut = snapshot
        self.calls.append('undo shortcut')

    def restore_task(self, name, snapshot):
        if snapshot is None:
            self.tasks.pop(name, None)
        else:
            self.tasks[name] = copy.deepcopy(snapshot)
        self.calls.append('undo ' + name)


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='discord-installer-tests-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        for name in installer.SOURCES:
            (self.root / name).write_text('test source', encoding='utf-8')
        (self.root / 'DiscordLinkFixer.exe').write_bytes(b'old executable')
        (self.root / 'install-receipt.json').write_bytes(b'old receipt')
        self.backend = Backend()
        self.builds = 0

    def build(self, root, destination):
        self.builds += 1
        path = destination / 'DiscordLinkFixer.exe'
        path.write_bytes(b'verified candidate')
        return path, {'passed': True}

    def run_install(self, **options):
        return installer.install(self.root, self.backend, builder=self.build, **options)

    def unchanged(self, tasks, shortcut):
        self.assertEqual(self.backend.tasks, tasks)
        self.assertEqual(self.backend.shortcut, shortcut)
        self.assertEqual((self.root / 'DiscordLinkFixer.exe').read_bytes(), b'old executable')
        self.assertEqual((self.root / 'install-receipt.json').read_bytes(), b'old receipt')

    def test_primary_conflict_before_build_or_mutation(self):
        self.backend.tasks[installer.TASK]['xml'] = 'foreign'
        with self.assertRaises(RuntimeError):
            self.run_install(update=True)
        self.assertEqual((self.builds, self.backend.calls), (0, []))

    def test_recovery_conflict_before_primary_mutation(self):
        self.backend.tasks[installer.LEGACY_RECOVERY]['xml'] = 'foreign'
        with self.assertRaises(RuntimeError):
            self.run_install(update=True)
        self.assertEqual((self.builds, self.backend.calls), (0, []))

    def test_shortcut_conflict_before_build_or_mutation(self):
        self.backend.shortcut = b'foreign'
        with self.assertRaises(RuntimeError):
            self.run_install(update=True)
        self.assertEqual((self.builds, self.backend.calls), (0, []))

    def test_explicit_update_required(self):
        with self.assertRaises(RuntimeError):
            self.run_install()
        self.assertEqual(self.backend.calls, [])

    def test_foreign_startup_shortcut_blocks_before_build(self):
        self.backend.startup_data = b'foreign'
        with self.assertRaisesRegex(RuntimeError, 'Startup shortcut ownership'):
            self.run_install(update=True)
        self.assertEqual((self.builds, self.backend.calls), (0, []))

    def test_concurrent_startup_edit_blocks_before_mutation(self):
        def concurrent(root, destination):
            result = self.build(root, destination)
            self.backend.startup_data = b'owned concurrently changed'
            return result
        with self.assertRaisesRegex(RuntimeError, 'changed during validation'):
            installer.install(self.root, self.backend, update=True, builder=concurrent)
        self.assertEqual(self.backend.calls, [])

    def test_running_helper_not_stopped(self):
        self.backend.live = True
        with self.assertRaises(RuntimeError):
            self.run_install(update=True)
        self.assertTrue(self.backend.live)
        self.assertEqual(self.backend.calls, [])

    def test_check_allowed_while_helper_running(self):
        self.backend.live = True
        result = self.run_install(update=True, check=True)
        self.assertFalse(result['installed'])
        self.assertTrue(self.backend.live)
        self.assertEqual(self.backend.calls, [])

    def test_build_failure_no_mutations(self):
        def fail(root, destination):
            raise RuntimeError('Compiler or self-test failed')
        with self.assertRaises(RuntimeError):
            installer.install(self.root, self.backend, update=True, builder=fail)
        self.assertEqual(self.backend.calls, [])

    def test_concurrent_start_rechecked(self):
        def concurrent(root, destination):
            result = self.build(root, destination)
            self.backend.live = True
            return result
        with self.assertRaises(RuntimeError):
            installer.install(self.root, self.backend, update=True, builder=concurrent)
        self.assertEqual(self.backend.calls, [])

    def test_concurrent_task_edit_rechecked(self):
        def concurrent(root, destination):
            result = self.build(root, destination)
            self.backend.tasks[installer.TASK]['xml'] = 'owned-concurrently-edited'
            return result
        with self.assertRaises(RuntimeError):
            installer.install(self.root, self.backend, update=True, builder=concurrent)
        self.assertEqual(self.backend.calls, [])

    def test_every_partial_mutation_rolls_back(self):
        tasks, shortcut = copy.deepcopy(self.backend.tasks), self.backend.shortcut
        for failure in ('task', 'recovery', 'shortcut', 'startup', 'started', 'ready'):
            with self.subTest(failure=failure):
                self.backend = Backend()
                self.backend.fail = failure
                with self.assertRaises(RuntimeError):
                    self.run_install(update=True)
                self.unchanged(tasks, shortcut)
                self.assertIsNone(self.backend.startup_data)
                self.assertFalse(self.backend.live)

    def test_initial_install_rollback_removes_only_new_artifacts(self):
        self.backend.tasks = {}
        self.backend.shortcut = None
        (self.root / 'DiscordLinkFixer.exe').unlink()
        (self.root / 'install-receipt.json').unlink()
        self.backend.fail = 'ready'
        with self.assertRaises(RuntimeError):
            self.run_install()
        self.assertEqual(self.backend.tasks, {})
        self.assertIsNone(self.backend.shortcut)
        self.assertFalse((self.root / 'DiscordLinkFixer.exe').exists())
        self.assertFalse((self.root / 'install-receipt.json').exists())
        self.assertIsNone(self.backend.startup_data)

    def test_existing_startup_restored_after_failed_update(self):
        self.backend.startup_data = b'owned old startup'
        self.backend.fail = 'ready'
        with self.assertRaises(RuntimeError):
            self.run_install(update=True)
        self.assertEqual(self.backend.startup_data, b'owned old startup')

    def test_success_removes_periodic_recovery_only(self):
        result = self.run_install(update=True)
        self.assertFalse(result['periodicRecoveryTask'])
        self.assertNotIn(installer.LEGACY_RECOVERY, self.backend.tasks)
        self.assertIn(installer.TASK, self.backend.tasks)
        self.assertEqual((self.root / 'DiscordLinkFixer.exe').read_bytes(), b'verified candidate')
        self.assertEqual(len(result['sourceHashes']), len(installer.SOURCES))
        self.assertEqual(self.backend.startup_data, b'owned startup')
        self.assertEqual(result['startupShortcut'], str(self.backend.startup))

    def test_real_startup_shortcut_target_and_ownership(self):
        link = SimpleNamespace(Targetpath='', Arguments='', WorkingDirectory='', Save=lambda: None)
        backend = installer.WindowsBackend.__new__(installer.WindowsBackend)
        backend.startup = self.root / 'Startup' / 'Discord Link Fixer.lnk'
        backend.shell = SimpleNamespace(CreateShortCut=lambda path: link)
        exe = self.root / 'DiscordLinkFixer.exe'
        backend.write_startup(exe)
        self.assertEqual(link.Arguments, '--startup')
        self.assertTrue(backend.owns_startup(b'snapshot', exe))
        for field, value in [('Arguments', '--login'), ('Targetpath', str(self.root / 'foreign.exe')),
                             ('WorkingDirectory', str(self.root / 'foreign'))]:
            saved = getattr(link, field)
            setattr(link, field, value)
            self.assertFalse(backend.owns_startup(b'snapshot', exe))
            setattr(link, field, saved)

    def test_task_retains_recovery_without_a_logon_trigger(self):
        task = SimpleNamespace(RegistrationInfo=SimpleNamespace(), Principal=SimpleNamespace(),
                               Settings=SimpleNamespace(IdleSettings=SimpleNamespace()),
                               Actions=SimpleNamespace(Create=lambda kind: SimpleNamespace()))
        # Deliberately has no Triggers member; adding a logon trigger must fail this gate.
        registered = []
        backend = installer.WindowsBackend.__new__(installer.WindowsBackend)
        backend.scheduler = SimpleNamespace(NewTask=lambda flags: task)
        backend.folder = SimpleNamespace(RegisterTaskDefinition=lambda *args: registered.append(args))
        backend.account = 'user'
        backend.register_task(self.root / 'DiscordLinkFixer.exe')
        self.assertEqual(task.Settings.RestartCount, 3)
        self.assertEqual(task.Settings.MultipleInstances, 2)
        self.assertEqual(task.Principal.LogonType, 3)
        self.assertEqual(len(registered), 1)

    def test_real_backend_uses_scalar_bstr_launch_token(self):
        values = []
        class Task:
            def Run(self, value):
                values.append(value)
        class Folder:
            def GetTask(self, name):
                self_name = installer.TASK
                if name != self_name:
                    raise RuntimeError('Unexpected task')
                return Task()
        backend = installer.WindowsBackend.__new__(installer.WindowsBackend)
        backend.folder = Folder()
        backend.start_task(self.root / 'DiscordLinkFixer.exe')
        self.assertIsInstance(values[0], str)
        self.assertRegex(values[0], r'^--install-token=[a-f0-9]{32}$')

    def test_process_identity_queries_ignore_unrelated_programs(self):
        exe = self.root / 'DiscordLinkFixer.exe'
        class Gone(Exception):
            pass
        class Process:
            def __init__(self, name, path=None, token=None, created=10):
                self.info = {'name': name}
                self.path, self.token, self.created = path, token, created
                self.stopped = False
            def exe(self):
                if self.path is None:
                    raise AssertionError('Unrelated process identity queried')
                return str(self.path)
            def create_time(self):
                return self.created
            def cmdline(self):
                return [self.token] if self.token else []
            def terminate(self):
                self.stopped = True
        unrelated = Process('AspenPlus.exe')
        other = Process('DiscordLinkFixer.exe', self.root / 'other' / exe.name, 'owned')
        old = Process('DiscordLinkFixer.exe', exe, 'owned', 1)
        untagged = Process('DiscordLinkFixer.exe', exe)
        owned = Process('DiscordLinkFixer.exe', exe, 'owned')
        processes = [unrelated, other, old, untagged, owned]
        def iterate(fields):
            self.assertEqual(fields, ['name'])
            return processes
        backend = installer.WindowsBackend.__new__(installer.WindowsBackend)
        backend.psutil = SimpleNamespace(process_iter=iterate, NoSuchProcess=Gone, wait_procs=lambda values, timeout: (values, []))
        backend.exe, backend.started_at, backend.token = exe, 10, 'owned'
        self.assertTrue(backend.running(exe))
        backend.stop_started()
        self.assertEqual([p.stopped for p in processes], [False, False, False, False, True])

    def test_matching_process_access_failure_blocks_update(self):
        class Denied:
            info = {'name': 'DiscordLinkFixer.exe'}
            def exe(self):
                raise PermissionError('Identity unavailable')
        backend = installer.WindowsBackend.__new__(installer.WindowsBackend)
        backend.psutil = SimpleNamespace(process_iter=lambda fields: [Denied()], NoSuchProcess=ProcessLookupError)
        with self.assertRaises(PermissionError):
            backend.running(self.root / 'DiscordLinkFixer.exe')

    def test_rollback_continues_after_shortcut_restore_failure(self):
        self.backend.fail = 'ready'
        def fail_restore(snapshot):
            raise PermissionError('Injected rollback failure')
        self.backend.restore_shortcut = fail_restore
        tasks = copy.deepcopy(self.backend.tasks)
        with self.assertRaisesRegex(RuntimeError, 'Rollback incomplete'):
            self.run_install(update=True)
        self.assertEqual(self.backend.tasks, tasks)
        self.assertEqual((self.root / 'DiscordLinkFixer.exe').read_bytes(), b'old executable')


if __name__ == '__main__':
    unittest.main()
