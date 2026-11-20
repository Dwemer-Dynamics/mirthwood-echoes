"""One-time, resumable rename of the pre-Echoes WSL installation. Run under the installer lock."""
import grp
import os
from pathlib import Path
import pwd
import subprocess
import time

OLD_ACCOUNT = "mirthwoodai"
ACCOUNT = "mirthwood_echoes"
OLD_UNIT = "mirthwood-ai.service"
STATE = Path("/var/lib/mirthwood-echoes")
APP = Path("/opt/mirthwood-echoes")
PENDING = Path("/var/lib/.mirthwood-echoes-identity-upgrade")


def run(*args, output=False):
    return subprocess.run(args, check=True, text=True, stdout=subprocess.PIPE if output else None).stdout


def sql(query):
    return run("runuser", "-u", "postgres", "--", "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1",
               "-d", "postgres", "-c", query, output=True).strip()


def known(lookup, name):
    try:
        return lookup(name)
    except KeyError:
        return None


def drain_wsl_login(user):
    """WSL can leave a login shell and its systemd user manager after a one-shot command."""
    def processes():
        owned = []
        for process in Path("/proc").iterdir():
            if process.name.isdigit():
                try:
                    if process.stat().st_uid == user.pw_uid:
                        owned.append(process)
                except FileNotFoundError:
                    continue
        return owned

    resident = processes()
    for process in resident:
        try:
            command = (process / "cmdline").read_bytes().split(b"\0")
            if command[:2] in ([b"/lib/systemd/systemd", b"--user"], [b"/usr/lib/systemd/systemd", b"--user"]) or command[0] == b"(sd-pam)":
                continue
            status, parent_id = (process / "stat").read_text().rsplit(")", 1)[1].split()[:2]
            if status == "Z":
                raise RuntimeError(f"Exited process {process.name} is waiting for its parent {parent_id} to reap it.")
            if command == [b"-bash", b""] and status == "S" and (process / "cwd").resolve() == Path(user.pw_dir):
                # WSL itself starts one parked auto-login directly from its PID-1 child.
                # A shell created by a terminal, sshd, getty or a user-invoked login has different ancestry.
                login = Path("/proc") / parent_id
                parent_command = (login / "cmdline").read_bytes().split(b"\0")
                init_id = (login / "stat").read_text().rsplit(")", 1)[1].split()[1]
                wsl_init = Path("/proc") / init_id
                if (parent_command[:2] == [b"/bin/login", b"-f"] and login.stat().st_uid == 0
                        and (wsl_init / "exe").readlink() == Path("/init")
                        and (wsl_init / "comm").read_text().startswith("init-systemd")
                        and (wsl_init / "stat").read_text().rsplit(")", 1)[1].split()[1] == "1"):
                    continue
        except FileNotFoundError:
            continue
        raise RuntimeError(f"The previous service account still has an active process ({process.name}).")
    if resident:
        run("loginctl", "terminate-user", str(user.pw_uid))
        for _ in range(80):
            if not processes():
                return
            time.sleep(.1)
        raise RuntimeError("The previous WSL login manager is still exiting. Retry the updater.")


def main():
    old_state = Path("/var/lib/mirthwoodai")
    old_app = Path("/opt/mirthwoodai")
    old_unit = Path("/etc/systemd/system") / OLD_UNIT
    old_env = Path("/etc/mirthwood-ai.env")
    paths = [(old_app, APP), (old_state, STATE),
             (old_unit, old_unit.with_name("mirthwood-echoes.service")),
             (old_env, old_env.with_name("mirthwood-echoes.env"))]
    old_user = known(pwd.getpwnam, OLD_ACCOUNT)
    old_group = known(grp.getgrnam, OLD_ACCOUNT)
    databases = set(sql("SELECT datname FROM pg_database WHERE datname IN ('mirthwoodai','mirthwood_echoes')").splitlines())
    roles = set(sql("SELECT rolname FROM pg_roles WHERE rolname IN ('mirthwoodai','mirthwood_echoes')").splitlines())
    if not (PENDING.exists() or old_user or old_group or OLD_ACCOUNT in databases | roles or any(p.exists() for p, _ in paths)):
        return

    # Refuse to merge two independent installations. A retry can encounter either
    # name for each resource after an interruption between individual renames.
    for source, dest in paths:
        if source.is_symlink() or dest.is_symlink() or (source.exists() and dest.exists()):
            raise RuntimeError(f"Resolve the installation path collision before upgrading: {source}, {dest}")
    if old_user and (known(pwd.getpwnam, ACCOUNT) or old_user.pw_dir != str(old_state)):
        raise RuntimeError("Ambiguous Unix account or home directory.")
    if old_group and known(grp.getgrnam, ACCOUNT):
        raise RuntimeError("Both Unix groups already exist.")
    if len(databases) > 1 or len(roles) > 1:
        raise RuntimeError("Both PostgreSQL identities already exist; refusing to merge independent data.")
    if OLD_ACCOUNT in roles and sql("SELECT count(*) FROM pg_authid WHERE rolname='mirthwoodai' AND rolpassword LIKE 'md5%'") != "0":
        raise RuntimeError("The previous database role uses a name-bound MD5 password. Convert it to SCRAM before renaming.")
    if old_unit.exists() and "ExecStart=/usr/bin/dotnet /opt/mirthwoodai/current/Mirthwood.Server.dll" not in old_unit.read_text():
        raise RuntimeError("The previous systemd unit is not the foundation installation.")
    old_current = old_app / "current"
    if old_current.is_symlink() and old_current.readlink().parent != old_app / "releases":
        raise RuntimeError("Unexpected previous release path.")

    active = old_unit.exists() and subprocess.run(["systemctl", "is-active", "--quiet", OLD_UNIT]).returncode == 0
    if not PENDING.exists() or (active and not PENDING.read_text().strip()):
        staged_marker = PENDING.with_suffix(".new")
        staged_marker.write_text("old-active\n" if active else "old-inactive\n")
        staged_marker.chmod(0o600)
        staged_marker.replace(PENDING)
    if old_unit.exists():
        run("systemctl", "stop", OLD_UNIT)
    try:
        if old_user:
            drain_wsl_login(old_user)
        commands = []
        if OLD_ACCOUNT in databases:
            commands.append("ALTER DATABASE mirthwoodai RENAME TO mirthwood_echoes")
        if OLD_ACCOUNT in roles:
            commands.append("ALTER ROLE mirthwoodai RENAME TO mirthwood_echoes")
        if commands:
            sql("BEGIN; " + "; ".join(commands) + "; COMMIT")
    except Exception:
        if old_unit.exists() and PENDING.read_text().strip() == "old-active" and OLD_ACCOUNT in databases and OLD_ACCOUNT in roles:
            run("systemctl", "start", OLD_UNIT)
        raise

    if old_unit.exists():
        run("systemctl", "disable", OLD_UNIT)
    if old_user:
        run("usermod", "-l", ACCOUNT, OLD_ACCOUNT)
    if old_group:
        run("groupmod", "-n", ACCOUNT, OLD_ACCOUNT)
    if known(pwd.getpwnam, ACCOUNT) and pwd.getpwnam(ACCOUNT).pw_dir != str(STATE):
        run("usermod", "-d", str(STATE), ACCOUNT)
    for source, dest in paths:
        if source.exists():
            source.rename(dest)
    current = APP / "current"
    if current.is_symlink() and current.readlink().parent == old_app / "releases":
        link = APP / ".echoes-release-next"
        if link.is_symlink():
            link.unlink()
        link.symlink_to(APP / "releases" / current.readlink().name)
        link.replace(current)
    if STATE.is_dir():
        (STATE / ".upgrade-provider-identity").touch(mode=0o600)
    run("systemctl", "daemon-reload")


if __name__ == "__main__":
    main()
