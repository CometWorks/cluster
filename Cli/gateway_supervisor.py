"""Linux gateway-rs restart loop with exact child identity and bounded local evidence."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import resource
import selectors
import signal
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone

LOG_BYTES = 8 << 20


def utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def write_json(path: Path, value: dict) -> None:
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        with temporary.open('x', encoding='utf-8') as output:
            os.chmod(temporary, 0o600)
            json.dump(value, output, separators=(',', ':'))
            output.flush()
            os.fsync(output.fileno())
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def dump_policy(env: dict[str, str]) -> tuple[bool, int, str | None]:
    """A missing, malformed, expired or unrelated Host lease never enables native cores."""
    try:
        with Path(env['ERROR_REPORTING_POLICY_FILE']).open('rb') as source:
            raw = source.read(65537)
        if len(raw) > 65536:
            return False, -1, None
        policy = json.loads(raw)
        context = json.loads(env.get('ERROR_REPORTING_CONTEXT', '{}'))
        if not isinstance(policy, dict) or not isinstance(context, dict):
            return False, -1, None
        now = datetime.now(timezone.utc)
        expires = datetime.fromisoformat(policy['expiresAtUtc'].replace('Z', '+00:00'))
        key_expires = datetime.fromisoformat(policy['keyExpiresAtUtc'].replace('Z', '+00:00'))
        granted = (policy.get('diagnosticsGranted') is True and policy.get('dumpsGranted') is True
                   and isinstance(policy.get('installationId'), str) and len(policy['installationId']) == 32
                   and ('installationId' not in context or policy['installationId'] == context['installationId'])
                   and now < expires <= now + timedelta(minutes=5)
                   and now < key_expires and type(policy.get('generation')) is int
                   and policy['generation'] >= 0)
        return (True, policy['generation'], policy['installationId']) if granted else (False, -1, None)
    except (OSError, ValueError, KeyError, TypeError, AttributeError, OverflowError):
        return False, -1, None


def record_identity(run: dict, executable: Path) -> dict:
    stat = Path('/proc/self/stat').read_text()
    ticks = int(stat[stat.rfind(')') + 1:].split()[19])
    boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    boot_time = next(int(line.split()[1]) for line in Path('/proc/stat').read_text().splitlines()
                     if line.startswith('btime '))
    # Scripts use their interpreter as COREDUMP_EXE; native gateway binaries resolve directly.
    try:
        with executable.open('rb') as source:
            prefix = source.read(256)
    except OSError:
        prefix = b''
    actual_executable = executable.resolve()
    if prefix.startswith(b'#!'):
        actual_executable = Path(prefix.splitlines()[0][2:].strip().split()[0].decode()).resolve()
    return {**run, 'processId': os.getpid(), 'kernelIdentity': f'{boot}/{ticks}',
            'processStartedAtUtc': datetime.fromtimestamp(boot_time + ticks / os.sysconf('SC_CLK_TCK'), timezone.utc).isoformat(),
            'executable': str(actual_executable)}


def update_run(directory: Path, **changes) -> dict | None:
    try:
        run = json.loads((directory / 'run.json').read_text())
        run.update(changes)
        write_json(directory / 'run.json', run)
        return run
    except (OSError, ValueError):
        return None


def output(directory: Path, data: bytes) -> None:
    try:
        log = directory / 'process.log'
        if log.exists() and log.stat().st_size + len(data) > LOG_BYTES:
            log.replace(directory / 'process.log.1')
        with log.open('ab', buffering=0) as stream:
            os.chmod(log, 0o600)
            stream.write(data)
    except OSError:
        pass
    try:
        os.write(sys.stdout.fileno(), data)
    except OSError:
        pass


def supervise(argv: list[str]) -> int:
    """Restart every unexpected exit after one second, until SIGTERM/SIGINT requests a stop."""
    executable = Path(argv[0]).resolve()
    base_env = dict(os.environ)
    inherited = base_env.get('ERROR_REPORTING_DIRECTORY')
    # Host scans direct children of runs/, so each native restart is a sibling of its wrapper.
    runs = Path(inherited).resolve().parent if inherited else Path.cwd() / 'diagnostics' / 'runs'
    try:
        runs.mkdir(parents=True, exist_ok=True, mode=0o700)
    except OSError as error:
        print(f'[cluster] gateway-rs diagnostics unavailable: {error}', file=sys.stderr, flush=True)
    stopping = False

    def stop(_signum, _frame):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    while not stopping:
        run_id = uuid.uuid4().hex
        directory = runs / run_id
        try:
            directory.mkdir(mode=0o700)
        except OSError:
            pass  # Failed evidence storage must not prevent the gateway from running.
        allowed, generation, installation = dump_policy(base_env)
        try:
            context = json.loads(base_env.get('ERROR_REPORTING_CONTEXT', '{}'))
        except ValueError:
            context = {}
        if not isinstance(context, dict):
            context = {}
        if installation is not None:
            # A wrapper started before the first Quasar lease has no inherited installation ID.
            context['installationId'] = installation
        context.pop('executableSha256', None)
        try:
            with executable.open('rb') as source:
                context['executableSha256'] = hashlib.file_digest(source, 'sha256').hexdigest()
        except OSError:
            pass  # Exec will report the actual launch failure; keep the normal restart loop.
        context.update(runId=run_id, role='gateway-rs', dumpsGrantedAtStart=str(allowed).lower(),
                       dumpConsentGeneration=str(generation))
        run = dict(runId=run_id, createdAtUtc=utc(), processId=None, processStartedAtUtc=None,
                   executable=str(executable), kernelIdentity=None, native=True, context=context,
                   endedAtUtc=None, expectedExit=False)
        env = {**base_env, 'ERROR_REPORTING_DIRECTORY': str(directory),
               'ERROR_REPORTING_CONTEXT': json.dumps(context)}
        for key in list(env):
            if key.startswith(('DOTNET_Dbg', 'COMPlus_Dbg')) or key in ('DOTNET_EnableCrashReport', 'CLUSTER_CORE_DUMPS'):
                env.pop(key)
        if allowed:
            env['RUST_BACKTRACE'] = '1'
        read_fd, write_fd = os.pipe()
        pid = os.fork()
        if pid == 0:
            try:
                os.close(read_fd)
                os.setpgid(0, 0)
                signal.signal(signal.SIGTERM, signal.SIG_DFL)
                signal.signal(signal.SIGINT, signal.SIG_DFL)
                os.dup2(write_fd, 1)
                os.dup2(write_fd, 2)
                os.close(write_fd)
                soft, hard = resource.getrlimit(resource.RLIMIT_CORE)
                resource.setrlimit(resource.RLIMIT_CORE, (hard if allowed else 0, hard))
                try:
                    write_json(directory / 'run.json', record_identity(run, executable))
                except (OSError, ValueError) as error:
                    print(f'[cluster] gateway-rs identity unavailable: {error}', file=sys.stderr, flush=True)
                os.execve(executable, argv, env)
            except BaseException as error:
                update_run(directory, native=False, executable=str(Path('/proc/self/exe').resolve()))
                print(f'[cluster] gateway-rs launch failed: {error}', file=sys.stderr, flush=True)
                os._exit(127)
        os.close(write_fd)
        selector = selectors.DefaultSelector()
        os.set_blocking(read_fd, False)
        selector.register(read_fd, selectors.EVENT_READ)
        sent_stop = False
        stop_deadline = 0.0
        status = None
        identity_deadline = time.monotonic() + 1.0
        try:
            while status is None:
                if stopping and not sent_stop:
                    # Identity is written before exec; if an immediate stop won that race, retry next tick.
                    if update_run(directory, expectedExit=True) is not None or time.monotonic() >= identity_deadline:
                        try:
                            os.killpg(pid, signal.SIGTERM)
                        except ProcessLookupError:
                            pass
                        sent_stop = True
                        stop_deadline = time.monotonic() + 1.0
                if sent_stop and time.monotonic() >= stop_deadline:
                    try:
                        os.killpg(pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                for key, _ in selector.select(0.1):
                    data = os.read(key.fd, 65536)
                    if data:
                        output(directory, data)
                    else:
                        selector.unregister(read_fd)
                ended, result = os.waitpid(pid, os.WNOHANG)
                if ended:
                    status = result
            # Drain only immediately available output; inherited pipes must not delay restarting.
            for _ in range(16):
                try:
                    data = os.read(read_fd, 65536)
                except BlockingIOError:
                    break
                if not data:
                    break
                output(directory, data)
        finally:
            selector.close()
            os.close(read_fd)
        code = os.waitstatus_to_exitcode(status)
        for log in (directory / 'process.log', directory / 'process.log.1'):
            try:
                with log.open('rb') as stream:
                    os.fsync(stream.fileno())
            except OSError:
                pass
        finished = update_run(directory, endedAtUtc=utc(), expectedExit=stopping)
        if not stopping and finished is not None and not (directory / 'exit-recorded').exists():
            try:
                write_json(directory / 'exit.json', dict(schemaVersion=1, reportId=uuid.uuid4().hex,
                    capturedAtUtc=utc(), source='gateway-rs', kind='unexpected_exit', severity='Error',
                    message=f'Process exited unexpectedly with code {code}.', exception=None,
                    context={**context, 'exitCode': str(code)}))
                (directory / 'exit-recorded').touch(mode=0o600)
            except OSError as error:
                print(f'[cluster] gateway-rs exit evidence unavailable: {error}', file=sys.stderr, flush=True)
        if not stopping:
            print(f'[cluster] gateway-rs exited with {code}; restarting in 1 s', flush=True)
            deadline = time.monotonic() + 1
            while not stopping and time.monotonic() < deadline:
                time.sleep(0.05)
    return 0


if __name__ == '__main__':
    sys.exit(supervise(sys.argv[1:]))
