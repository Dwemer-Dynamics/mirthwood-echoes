"""Test a provider-key migration using the previous release ZIP and a published Echoes package in WSL."""
from contextlib import contextmanager
import http.server
import json
import os
from pathlib import Path
import pwd
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
import zipfile

from foundation_smoke import fixture_save, free_port


def main(previous_zip, package):
    account = pwd.getpwnam("mirthwood_echoes")
    database = "mirthwood_echoes_keytest_" + uuid.uuid4().hex[:12]
    secret = uuid.uuid4().hex
    port = free_port()

    class Provider(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_POST(self):
            packet=json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            if self.headers.get("Authorization") != "Bearer " + secret:
                self.send_error(401)
                return
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            content="Welcome back."
            if 'The FIRST field must be "reply"' in packet["messages"][0]["content"]:
                content=json.dumps({"reply":content,"commitments":[]})
            self.wfile.write(json.dumps({"choices":[{"message":{"content":content}}]}).encode())

    def api(path, data=None, method=None):
        request = urllib.request.Request("http://127.0.0.1:" + str(port) + path, method=method,
                                         data=json.dumps(data).encode() if data is not None else None,
                                         headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=10) as response:
            data = response.read()
            if "ndjson" in response.headers.get("Content-Type", ""):
                return [json.loads(line) for line in data.splitlines()]
            return json.loads(data) if data else None

    def postgres(*args):
        return subprocess.check_output(["runuser", "-u", "postgres", "--", *args], text=True).strip()

    def digest():
        return postgres("psql", "-XAt", "-d", database, "-c",
                        "SELECT md5(value->>'secret') FROM configuration WHERE key='profile/openai_compatible'")

    with tempfile.TemporaryDirectory(prefix="echoes-keytest-") as directory, \
            http.server.ThreadingHTTPServer(("127.0.0.1", 0), Provider) as provider:
        tmp = Path(directory)
        tmp.chmod(0o755)
        old, state, saves = tmp / "previous", tmp / "state", tmp / "saves"
        for folder in (old, state, saves):
            folder.mkdir()
        os.chown(state, account.pw_uid, account.pw_gid)
        marker = state / ".upgrade-provider-identity"
        fixture_save(saves, 0, "2025-01-02T12:00:00")
        with zipfile.ZipFile(previous_zip) as archive:
            for name in archive.namelist():
                member = Path(name)
                if member.parent == Path("app") and member.suffix in {".dll", ".json"}:
                    (old / member.name).write_bytes(archive.read(name))

        @contextmanager
        def service(assembly, prefix, version):
            config = {"DB": "Host=/var/run/postgresql;Username=mirthwood_echoes;Database=" + database,
                      "PORT": str(port), "GAME": str(tmp / "no-game"), "SAVES": str(saves), "STATE": str(state)}
            env = dict(os.environ, HOME=str(state), DOTNET_PROCESSOR_COUNT="2", Logging__LogLevel__Default="Warning")
            env.update({prefix + "_" + key: value for key, value in config.items()})
            with subprocess.Popen(["/usr/bin/dotnet", str(assembly)], cwd=assembly.parent, env=env,
                                  user=account.pw_uid, group=account.pw_gid, extra_groups=[],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as process:
                try:
                    for _ in range(100):
                        if process.poll() is not None:
                            raise RuntimeError(f"Release {version} exited during startup ({process.returncode}).")
                        try:
                            health = api("/api/health")
                            if prefix == "MIRTHWOOD" and health.get("application") == "mirthwood-ai":
                                break
                            if health.get("version") == version and health.get("application") == "mirthwood-echoes":
                                break
                        except urllib.error.URLError:
                            pass
                        time.sleep(.1)
                    else:
                        raise TimeoutError(f"Release {version} did not start.")
                    yield
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=8)
                    except subprocess.TimeoutExpired:
                        process.kill()

        def verify_use():
            profile = api("/api/providers/openai_compatible")
            assert profile["hasKey"] and secret not in json.dumps(profile)
            api("/api/saves/refresh", {})
            campaign = api("/api/saves")[0]["id"]
            roster = api("/api/saves/" + campaign + "/characters")
            actor = next(row["key"] for row in roster if row["owner"] == "East-NPC")
            reply = api("/api/dialogue", {"id": str(uuid.uuid4()), "campaign": campaign, "actor": actor, "text": "Hello."})
            assert reply[-1]["type"] == "done" and "".join(r["text"] for r in reply if r["type"] == "delta") == "Welcome back."
            return campaign,actor

        threading.Thread(target=provider.serve_forever, daemon=True).start()
        postgres("createdb", "-O", account.pw_name, database)
        try:
            with service(old / "Mirthwood.Server.dll", "MIRTHWOOD", "0.1.1"):
                api("/api/providers/openai_compatible", {"apiKey": secret, "defaultModel": "test",
                    "endpoint": f"http://127.0.0.1:{provider.server_port}/v1/chat/completions"}, "PUT")
                api("/api/options", {"provider": "openai_compatible", "replyTokens": 80, "dailyDays": 2}, "PUT")
                campaign,actor=verify_use()
                old_words="I promise to return the kiln tools tomorrow."
                api("/api/dialogue",{"id":str(uuid.uuid4()),"campaign":campaign,"actor":actor,"text":old_words})
                original = digest()
                assert len(original) == 32
            assembly = package / "app" / "Mirthwood.Echoes.Server.dll"
            version = json.loads((package / "app/bundle/manifest.json").read_text())["version"]
            marker.touch()
            with service(assembly, "MIRTHWOOD_ECHOES", version):
                verify_use()
                old_row=next(x for x in api("/api/saves/"+campaign+"/characters/"+actor+"/history/commitments")["items"] if x["statement"]==old_words)
                assert old_row["speaker"]=="player" and old_row["sourceTurn"] is None and old_row["model"] is None
                renamed = digest()
                assert len(renamed) == 32 and renamed != original and not marker.exists()
            marker.touch()
            with service(assembly, "MIRTHWOOD_ECHOES", version):
                verify_use()
                assert digest() == renamed and not marker.exists()
            print("PASS 0.1.1 provider key migrates to Echoes and authenticates a dialogue request.")
            print("PASS retry after a committed migration keeps the same ciphertext and authenticates again.")
            print("PASS a real 0.1.1 promise survives the upgrade with its original wording and without invented model provenance.")
        finally:
            provider.shutdown()
            postgres("dropdb", "--if-exists", database)


if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
