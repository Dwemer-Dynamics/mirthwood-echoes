"""Run in the ReignServer WSL distro as root against a published package.

Creates an isolated role-owned PostgreSQL database, temporary Windows game folder,
and a loopback model fixture. Setting MIRTHWOOD_ECHOES_LIVE_CODEX_MODEL and
MIRTHWOOD_ECHOES_LIVE_CODEX_REPORT adds two signed-in Windows Codex exchanges.
"""
import base64
import contextlib
import hashlib
import http.server
import json
import os
from pathlib import Path
import pwd
import runpy
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
from unittest.mock import patch


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class Model(http.server.BaseHTTPRequestHandler):
    calls = []
    mode = "stream"
    samples = {}
    gates = {}

    def log_message(self, *args):
        pass

    def do_POST(self):
        packet = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self.calls.append(packet)
        player=packet["messages"][1]["content"]
        if gate := self.gates.get(player):
            started, release = gate
            started.set()
            if not release.wait(20):
                self.send_error(504, "The fixture reply was never released.")
                return
        marks=[{"speaker":"player","quote":player}] if player in ("I promise to deliver flour tomorrow.","I promise to dig a ditch tomorrow.") else []
        sample_key=player
        if "Mirthwood quest" in packet["messages"][0]["content"]:
            context=json.loads(player)
            sample_key="quest/"+("opening" if context["opening"] else context["latestPlayerSpeech"])
        elif '"heardInOrder":' in player:
            sample_key="group speaker"
        elif '"cue":' in player and '"card":' in player:
            sample_key="passing speaker"
        wire=self.samples.get(sample_key,json.dumps({"reply":"The mill is open.","commitments":marks}))
        self.send_response(200)
        self.send_header("Content-Type", "application/json" if self.mode == "json" else "text/event-stream")
        self.end_headers()
        if self.mode == "json":
            self.wfile.write(json.dumps({"choices":[{"message":{"content":wire}}]}).encode())
            return
        split=wire.find("is open.")
        pieces=[wire[:split],wire[split:]] if split>0 else [wire]
        if self.mode=="fragmented":pieces=list(wire)
        for delta in pieces:
            msg = {"choices": [{"delta": {"content": delta, "reasoning_content": "private reasoning"}}]}
            self.wfile.write(("data: " + json.dumps(msg) + "\n\n").encode())
            self.wfile.flush()
            if self.mode!="fragmented":time.sleep(.075)
        if self.mode != "interrupted":
            self.wfile.write(b"data: [DONE]\n\n")


def fixture_save(saves, slot, creation, day=10, visitor=False):
    def row(owner, component, payload):
        return {"guid": owner + "-" + component + "-abc", "scene": "Village", "data": json.dumps(payload)}
    def info(name, ident):
        return {"characterInfo": {"name": name, "id": ident, "job": 11, "town": 0, "personality": {"values": [0.3]}}}
    records = [row("Player", "PlayerCharacterController", info("Test player", "p")),
               row("East-NPC", "NPCController", info("Miller", "twin")), row("West-NPC", "NPCController", info("Miller", "twin")),
               row("East-NPC", "Inventory", {"future-key": [14, None, {"nested": False}]}),
               row("Clock", "DayNightCycle", {"dayNumber": day, "yearNumber": 0})]
    if visitor:
        records.append(row("Visitor-NPC","NPCController",info("Linn","linn")))
    path = saves / ("Slot" + str(slot) + ".savegame")
    path.write_text(json.dumps({"metaData": {"creationDate": creation}, "saveData": records}))
    return path


def main(package, workspace):
    account = pwd.getpwnam("mirthwood_echoes")
    token = uuid.uuid4().hex[:12]
    database = "mirthwood_echoes_test_" + token
    port = free_port()
    base = "http://127.0.0.1:" + str(port)
    model = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Model)
    threading.Thread(target=model.serve_forever, daemon=True).start()
    tests = []

    def api(path, data=None, method=None, status=200, headers=None):
        req = urllib.request.Request(base + path, method=method, data=json.dumps(data).encode() if data is not None else None,
                                     headers={"Content-Type": "application/json", **(headers or {})})
        try:
            response = urllib.request.urlopen(req, timeout=22)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read()
            assert response.status == status, (path, response.status, body[:400])
            if "ndjson" in response.headers.get("Content-Type", ""):
                return [json.loads(x) for x in body.splitlines()]
            return json.loads(body) if body else None

    def postgres(*args):
        return subprocess.run(["runuser", "-u", "postgres", "--", *args], check=True, capture_output=True, text=True).stdout

    def checked(name):
        tests.append(name)
        print("PASS", name, flush=True)

    try:
        with tempfile.TemporaryDirectory(prefix="mw-foundation-") as local, tempfile.TemporaryDirectory(prefix="game-", dir=workspace) as windows:
            tmp, game = Path(local), Path(windows)
            tmp.chmod(0o755)
            app = tmp / "app"
            shutil.copytree(package / "app", app, ignore=shutil.ignore_patterns("bundle", "*.pdb"))
            state, saves, bundle = tmp / "state", tmp / "saves", app / "bundle"
            for folder in [state, saves, bundle]:
                folder.mkdir()
            os.chown(state, account.pw_uid, account.pw_gid)
            game_exe = game / "Mirthwood.exe"
            game_exe.write_bytes(b"fixture")
            assembly = game / "Mirthwood_Data/Managed/Assembly-CSharp.dll"
            assembly.parent.mkdir(parents=True)
            assembly.touch()
            module = "BepInEx/plugins/MirthwoodEchoes/Mirthwood.Echoes.dll"
            legacy_module = "BepInEx/plugins/MirthwoodAI/MirthwoodAI.dll"
            def release(version, binary):
                files = []
                for name, data, kind in [(module, binary, "module"), ("doorstop_config.ini", b"[General]\nenabled=true", "default")]:
                    dest = bundle / name
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(data)
                    files.append({"path": name, "sha256": hashlib.sha256(data).hexdigest().upper(), "kind": kind})
                (bundle / "manifest.json").write_text(json.dumps({"id": "mirthwood-echoes", "name": "Mirthwood: Echoes", "version": version, "files": files}))
            release("probe-1", b"module one")
            with patch.dict(os.environ,{"MIRTHWOOD_ECHOES_APP":str(app),"MIRTHWOOD_ECHOES_GAME":str(game)}):
                module_status=runpy.run_path(str(app/"web-control.py"))["module_status"]
            assert module_status()["state"]=="missing"
            created = "2025-01-02T12:00:00"
            fixture_save(saves, 0, created)
            fixture_save(saves, 1, "2025-01-03T12:00:00", visitor=True)
            postgres("createdb", "-O", "mirthwood_echoes", database)
            codex_probe=tmp/"codex"
            codex_probe.write_text("""#!/usr/bin/python3
import json,os,pathlib,sys
if not (pathlib.Path(__file__).parent/'use-rpc-fixture').exists():
    os.execv('/usr/local/bin/codex',['codex',*sys.argv[1:]])
def send(x):print(json.dumps(x),flush=True)
for line in sys.stdin:
    req=json.loads(line);method=req['method']
    if 'id' not in req:continue
    result={}
    if method=='initialize':
        assert req['params']['clientInfo']['name']=='mirthwood-echoes' and req['params']['clientInfo']['title']=='Mirthwood: Echoes'
    if method=='account/read':result={'account':{'type':'chatgpt'}}
    if method=='thread/start':
        assert req['params']['sandbox']=='read-only' and req['params']['ephemeral']
        result={'thread':{'id':'fixture-thread'}}
    if method=='turn/start':result={'turn':{'id':'fixture-turn'}}
    send({'id':req['id'],'result':result})
    if method=='turn/start':
        for thread,text in [('other','Wrong actor'),('fixture-thread',json.dumps({'reply':'Fresh bread.','commitments':[]}))]:
            send({'method':'item/agentMessage/delta','params':{'threadId':thread,'delta':text}})
        send({'method':'turn/completed','params':{'threadId':'fixture-thread','turn':{'status':'completed'}}})
""")
            codex_probe.chmod(0o755)
            env = dict(os.environ, MIRTHWOOD_ECHOES_DB="Host=/var/run/postgresql;Username=mirthwood_echoes;Database="+database,
                       MIRTHWOOD_ECHOES_STATE=str(state), MIRTHWOOD_ECHOES_GAME=str(game), MIRTHWOOD_ECHOES_SAVES=str(saves),
                       MIRTHWOOD_ECHOES_PORT=str(port), HOME=str(state), DOTNET_PROCESSOR_COUNT="2",
                       PATH=str(tmp)+":"+os.environ["PATH"], Logging__LogLevel__Default="Warning", WSL_INTEROP="/run/WSL/1_interop")
            with (tmp/"server.log").open("w+") as log:
                proc = subprocess.Popen(["/usr/bin/dotnet", str(app/"Mirthwood.Echoes.Server.dll")], cwd=app, user=account.pw_uid,
                                        group=account.pw_gid, extra_groups=[], env=env, stdout=log, stderr=log)
                try:
                    for _ in range(70):
                        try:
                            if api("/api/health")["application"] == "mirthwood-echoes": break
                        except OSError: time.sleep(.15)
                    def check(): return api("/api/module/check", {})["status"]
                    health=api("/api/health")
                    assert health["application"]=="mirthwood-echoes" and health["displayName"]=="Mirthwood: Echoes"
                    assert check() == "ready"
                    assert module_status()["state"]=="current"
                    assert (game/module).read_bytes() == b"module one"
                    stamp=(game/module).stat().st_mtime_ns
                    assert check()=="ready" and stamp==(game/module).stat().st_mtime_ns
                    checked("fresh install and idempotent repeat")
                    config_dir = game / "BepInEx/config"
                    config_dir.mkdir(exist_ok=True)
                    old_config = config_dir / "mirthwoodai.companion.cfg"
                    new_config = config_dir / "mirthwood.echoes.cfg"
                    settings = b"# player settings\r\n[Controls]\r\nTalk = F7\r\n"
                    old_config.write_bytes(settings)
                    assert check() == "ready" and not old_config.exists() and new_config.read_bytes() == settings
                    old_config.write_bytes(b"another configuration")
                    assert check() == "conflict" and old_config.read_bytes() == b"another configuration" and new_config.read_bytes() == settings
                    old_config.write_bytes(settings)
                    assert check() == "ready" and not old_config.exists() and new_config.read_bytes() == settings
                    checked("plugin settings survive the GUID change and conflicting settings remain intact")
                    old_file=game/legacy_module
                    old_file.parent.mkdir()
                    sibling=old_file.parent/"other-mod.txt"
                    sibling.write_text("belongs to the player")
                    ledger=next(state.glob("module-*.json"))
                    ownership=json.loads(ledger.read_text())
                    ownership[legacy_module]=ownership[module]
                    ledger.write_text(json.dumps(ownership))
                    old_file.write_bytes(b"module one")
                    assert check()=="ready" and not old_file.exists() and sibling.is_file()
                    checked("partial rename with both DLLs present converges to one Echoes module")
                    (game/module).rename(old_file)
                    ownership[legacy_module]=ownership.pop(module)
                    ledger.write_text(json.dumps(ownership))
                    old_file.write_bytes(b"independently changed module")
                    assert check()=="conflict" and not (game/module).exists() and old_file.read_bytes()==b"independently changed module"
                    old_file.write_bytes(b"module one")
                    (game/"doorstop_config.ini").write_text("[General]\nenabled=true\n# local preference\n")
                    release("probe-2",b"module two")
                    assert module_status()["state"]=="update"
                    win_path=subprocess.check_output(["wslpath","-w",str(game_exe)],text=True).strip()
                    win_b64=base64.b64encode(win_path.encode()).decode()
                    ps="$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+win_b64+"'));$f=[IO.File]::Open($p,'Open','Read','None');[Console]::WriteLine('locked');[Console]::Out.Flush();[void][Console]::ReadLine();$f.Close()"
                    args=["/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe","-NoProfile","-NonInteractive","-EncodedCommand",base64.b64encode(ps.encode("utf-16le")).decode()]
                    with subprocess.Popen(args,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,env=env) as lock:
                        assert lock.stdout.readline().strip()==b"locked"
                        assert check()=="waiting_for_game_exit" and old_file.read_bytes()==b"module one" and not (game/module).exists()
                        lock.stdin.close()
                    # Let the background service apply it. Poll only the read-only status endpoint.
                    for _ in range(85):
                        if api("/api/health")["module"]["status"]=="ready" and (game/module).read_bytes()==b"module two":break
                        time.sleep(.5)
                    else:raise AssertionError("Queued update did not apply after the lock was released.")
                    assert "local preference" in (game/"doorstop_config.ini").read_text()
                    assert module_status()["state"]=="current"
                    checked("console distinguishes Install, Update and Check from actual module hash and preserves player config")
                    assert not old_file.exists() and legacy_module not in json.loads(ledger.read_text())
                    assert sibling.read_text()=="belongs to the player"
                    checked("owned legacy DLL is renamed after game exit; modified DLLs and neighboring files are protected")
                    checked("queued upgrade across an actual Windows file lock, followed by automatic replacement")
                    release("probe-3",b"module three")
                    (bundle/module).write_bytes(b"damaged")
                    assert check()=="error" and (game/module).read_bytes()==b"module two"
                    (bundle/module).write_bytes(b"module three")
                    (game/module).write_bytes(b"unmanaged")
                    assert check()=="conflict" and (game/module).read_bytes()==b"unmanaged"
                    checked("corrupt package and externally modified module are preserved for review")
                    (game/module).write_bytes(b"module three")
                    assert check()=="ready"
                    ledger=next(state.glob("module-*.json"))
                    old_hash=hashlib.sha256(b"module three").hexdigest().upper()
                    assert json.loads(ledger.read_text())[module]==old_hash
                    release("probe-4",b"module four")
                    new_hash=hashlib.sha256(b"module four").hexdigest().upper()
                    journal=Path(str(ledger)+".pending")
                    journal.write_text(json.dumps({"path":module,"before":old_hash,"after":new_hash}))
                    (game/module).write_bytes(b"module four")
                    assert check()=="ready" and json.loads(ledger.read_text())[module]==new_hash and not journal.exists()
                    checked("adoption of an exact pre-installed module and recovery after an interrupted rename")
                    api("/api/saves/refresh",{})
                    slots=api("/api/saves"); c,c2=slots[0]["id"],slots[1]["id"]; assert c!=c2
                    roster=api("/api/saves/"+c+"/characters")
                    a=next(x["key"] for x in roster if x["owner"]=="East-NPC")
                    twin=next(x["key"] for x in roster if x["owner"]=="West-NPC")
                    raw=api("/api/saves/"+c+"/characters/"+a)
                    assert raw["components"][0]["data"]["future-key"][2]["nested"] is False
                    api("/api/options",headers={"Origin":"https://unrelated.invalid"},status=403)
                    assert len(api("/api/providers"))==5
                    endpoint="http://127.0.0.1:"+str(model.server_port)+"/v1/chat/completions"
                    profile={"defaultModel":"town-default","conversation":"  ","memory":"archivist","endpoint":endpoint,"apiKey":"fixture-only"}
                    api("/api/providers/openai_compatible",profile,"PUT",204)
                    view=api("/api/providers/openai_compatible")
                    assert view["hasKey"] and "fixture-only" not in json.dumps(view) and view["memory"]=="archivist"
                    api("/api/options",{"provider":"openai_compatible","replyTokens":80,"dailyDays":2},"PUT",204)
                    checked("5 provider IDs, per-purpose inheritance and secret masking")
                    w={"slot":0,"created":created,"player":"p","day":10}
                    message={"id":str(uuid.uuid4()),"world":w,"owner":"East-NPC","nativeId":"twin","kind":"work","description":"Gathered barley for the mill."}
                    api("/api/module/resolve",dict(message,owner=""),status=422)
                    api("/api/module/event",message);api("/api/module/event",message)
                    def beat():api("/api/module/heartbeat",{"version":"fixture","hooks":2,"dropped":0,"world":w})
                    beat()
                    def say(text,actor=a,camp=c):
                        return {"campaign":camp,"actor":actor,"id":str(uuid.uuid4()),"text":text}
                    turn=say("I promise to deliver flour tomorrow.")
                    packet=api("/api/dialogue",turn)
                    assert [x["type"] for x in packet]==["delta","delta","done"] and packet[-1]["firstMs"]<packet[-1]["totalMs"]
                    assert len(Model.calls)==1 and Model.calls[-1]["model"]=="town-default" and Model.calls[-1]["max_tokens"]==80+384
                    assert "Gathered barley" in Model.calls[-1]["messages"][0]["content"] and "private reasoning" not in str(packet)
                    assert api("/api/dialogue",turn)[-1]["cached"] and len(Model.calls)==1
                    api("/api/dialogue",dict(turn,actor=twin),status=409)
                    checked("one streamed ordinary turn, first readable text before completion and replay without another request")
                    w["day"]=14;beat()
                    api("/api/dialogue",say("What about the flour?"))
                    prompt=Model.calls[-1]["messages"][0]["content"]
                    assert "deliver flour tomorrow" in prompt and '"due":11' in prompt and "Gathered barley" not in prompt
                    for actor,campaign in [(twin,c),(a,c2)]:
                        api("/api/dialogue",say("What do you remember?",actor,campaign))
                        prompt=Model.calls[-1]["messages"][0]["content"]
                        assert "deliver flour" not in prompt and "Gathered barley" not in prompt
                    checked("flour promise survives day 14, daily work fades, twin and other save remain isolated")
                    profile.update(conversation="dialogue-override",apiKey="")
                    api("/api/providers/openai_compatible",profile,"PUT",204)
                    Model.mode="json"
                    api("/api/dialogue",say("Hello there."))
                    assert Model.calls[-1]["model"]=="dialogue-override"
                    Model.mode="interrupted"
                    failed=api("/api/dialogue",say("I promise to dig a ditch tomorrow."))
                    assert failed[-1]["type"]=="error"
                    counts=api("/api/diagnostics")["counts"]
                    assert counts["commitments"]==1 and counts["observations"]==1
                    promises="/api/saves/"+c+"/characters/"+a+"/commitments"
                    promise=api(promises)[0]; assert promise["due"]==11
                    closure="/api/commitments/"+promise["id"]+"/fulfill"
                    api(closure,{"campaign":c,"actor":twin},status=409)
                    api(closure,{"campaign":c,"actor":a},status=204)
                    assert api(promises)==[]
                    w["day"]=12;beat();assert api(promises)[0]["id"]==promise["id"]
                    w["day"]=14;beat()
                    checked("fulfilment is explicitly attributed and respects a rewind to an earlier day")
                    from commitment_stream import check_commitments
                    check_commitments(api,Model,say,c,a,w,beat,checked)
                    from talk_retries import check_talk_retries
                    def legacy_packet(turn_id):
                        postgres("psql","-d",database,"-c",
                                 "UPDATE turns SET talk=talk-'positiveDesireCostPercent'-'endingChoices' WHERE state='ready' AND id='%s'"%uuid.UUID(turn_id))
                    check_talk_retries(api,Model,say,twin,checked,legacy_packet)
                    from native_interactions import check_context
                    check_context(api,Model,say,w,a,twin,checked)
                    from quest_dialogue import check_quest_guards
                    check_quest_guards(api,Model,say,twin,checked)
                    from world_awareness import check_world_awareness
                    check_world_awareness(api,Model,c2,a,twin,checked)
                    profile["endpoint"]=endpoint.replace("127.0.0.1","localhost")
                    api("/api/providers/openai_compatible",profile,"PUT",204)
                    assert api("/api/providers/openai_compatible")["hasKey"] is False
                    checked("complete JSON fallback, interrupted-stream handling, credential origin binding")
                    for provider in ["nanogpt","openrouter","ollama_cloud","codex_subscription"]:
                        api("/api/providers/"+provider,{"defaultModel":"test-only"},"PUT",204)
                        api("/api/options",{"provider":provider,"replyTokens":80,"dailyDays":2},"PUT",204)
                        err=api("/api/dialogue",say("Hello."),status=409)
                        assert ("API key" if provider!="codex_subscription" else "subscription sign-in") in err["error"]
                    checked("three named HTTP providers and the installed Codex app-server reject missing credentials before generation")
                    codex_path="/api/providers/codex_subscription"
                    assert api(codex_path)["codexHost"]=="service"
                    api(codex_path,{"defaultModel":"test-only","codexHost":"windows"},"PUT",204)
                    api(codex_path,{"defaultModel":"test-only"},"PUT",204)
                    assert api(codex_path)["codexHost"]=="windows"
                    api(codex_path,{"defaultModel":"test-only","codexHost":"unlisted"},"PUT",400)
                    api(codex_path,{"defaultModel":"test-only","codexHost":"service"},"PUT",204)
                    checked("Codex sign-in selection persists, rejects unknown hosts and preserves existing service profiles")
                    (tmp/"use-rpc-fixture").touch()
                    chunks=api("/api/dialogue",say("What is in your oven?"))
                    assert [p.get("text") for p in chunks if p["type"]=="delta"]==["Fresh bread."] and chunks[-1]["type"]=="done"
                    checked("subscription protocol fixture streams only the selected thread's dialogue")
                    live_model=os.environ.get("MIRTHWOOD_ECHOES_LIVE_CODEX_MODEL")
                    if live_model:
                        report=Path(os.environ["MIRTHWOOD_ECHOES_LIVE_CODEX_REPORT"])
                        api(codex_path,{"defaultModel":live_model,"codexHost":"windows"},"PUT",204)
                        samples=[]
                        for words in ("When work is done, would you rather fish or play chess?",
                                      "I promise to return your blue fishing net tomorrow."):
                            beat()
                            request_turn=say(words)
                            req=urllib.request.Request(base+"/api/dialogue",json.dumps(request_turn).encode(),
                                {"Content-Type":"application/json"})
                            with urllib.request.urlopen(req,timeout=90) as response:
                                packets=[json.loads(line) for line in response if line.strip()]
                            assert packets[-1]["type"]=="done",packets[-1]
                            speech="".join(p["text"] for p in packets if p["type"]=="delta")
                            stored=api("/api/saves/"+c+"/characters/"+a+"/history/conversations")["items"]
                            assert speech and any(r["id"]==request_turn["id"] and r["reply"]==speech for r in stored)
                            samples.append({"player":words,"reply":speech,"deltas":len(packets)-1,**packets[-1]})
                        entries=api("/api/saves/"+c+"/characters/"+a+"/history/commitments")["items"]
                        marked=next(p for p in entries if p["statement"]==samples[-1]["player"])
                        assert marked["provider"]=="codex_subscription" and marked["model"]==live_model
                        report.write_text(json.dumps({"provider":"codex_subscription","host":"windows",
                            "model":live_model,"server":"WSL as mirthwood_echoes","cases":samples,
                            "commitment":{"speaker":marked["speaker"],"quote":marked["statement"],"model":marked["model"]}},
                            ensure_ascii=False,indent=2),encoding="utf-8")
                        checked("published WSL dialogue streams and journals two actual Windows Codex replies and attributes a promise to its model")
                    (saves/"Slot0.savegame.b").write_bytes(b"broken")
                    scan=api("/api/saves/refresh",{})
                    assert "Slot0.savegame.b" in scan["detail"] and len(api("/api/saves/"+c+"/characters"))==3
                    checked("incomplete latest save falls back to its verified sibling")
                    def native_string(value):
                        data=value.encode();length=len(data);prefix=bytearray()
                        while length>=128:prefix.append((length&127)|128);length>>=7
                        prefix.append(length);return bytes(prefix)+data
                    doc=json.loads((saves/"Slot1.savegame").read_text())
                    (saves/"Slot2.savegame").write_bytes(native_string(json.dumps(doc)))
                    blob=native_string("binary")+native_string(doc["metaData"]["creationDate"])+struct.pack("<i",0)+native_string("0:00:00")+native_string(created)+struct.pack("<i",len(doc["saveData"]))
                    for r in doc["saveData"]:blob+=native_string(r["data"])+native_string(r["guid"])+native_string(r["scene"])
                    (saves/"Slot3.savegame").write_bytes(blob)
                    api("/api/saves/refresh",{})
                    all_slots=api("/api/saves");assert len(all_slots)==4
                    for other in all_slots[2:]:
                        assert len(api("/api/saves/"+other["id"]+"/characters"))==4
                    checked("legacy length-prefixed JSON and native binary component import")
                    late_save=fixture_save(saves,4,created,10)
                    api("/api/saves/refresh",{})
                    newcomer={"owner":"Newcomer-NPC","nativeId":"successor","description":"I received the red scarf."}
                    old={"id":str(uuid.uuid4()),"world":dict(w,slot=4,day=10),"kind":"gift","people":[newcomer]}
                    api("/api/module/cue",old);api("/api/module/cue",old)
                    body=json.loads(late_save.read_text())
                    resident=next(x for x in body["saveData"] if x["guid"].startswith("East-NPC-NPCController"))
                    replacement=dict(resident,guid=resident["guid"].replace("East-NPC","Newcomer-NPC"))
                    detail=json.loads(replacement["data"]);detail["characterInfo"]["id"]="successor"
                    replacement["data"]=json.dumps(detail);body["saveData"].append(replacement)
                    late_save.write_text(json.dumps(body));api("/api/saves/refresh",{})
                    discovered=api("/api/module/resolve",dict(old,owner="Newcomer-NPC",nativeId="successor"))
                    api("/api/module/heartbeat",{"version":"fixture","world":dict(old["world"],cueHead=old["id"])})
                    late_base="/api/saves/"+discovered["campaign"]+"/characters/"
                    assert len([x for x in api(late_base+discovered["actor"]+"/history/interactions")["items"] if x.get("description")==newcomer["description"]])==1
                    for namesake in api("/api/saves/"+discovered["campaign"]+"/characters"):
                        if namesake["key"]!=discovered["actor"]:
                            assert "red scarf" not in json.dumps(api(late_base+namesake["key"]+"/history/interactions"))
                    checked("an event arriving before roster import reaches only the newly matched resident once")
                    print("Test groups:",len(tests),"· local fixture calls:",len(Model.calls),flush=True)
                except BaseException:
                    log.flush(); log.seek(0); print(log.read()[-3000:],file=sys.stderr); raise
                finally:
                    proc.terminate()
                    try:proc.wait(timeout=8)
                    except subprocess.TimeoutExpired:proc.kill();proc.wait()
    finally:
        model.shutdown();model.server_close()
        postgres("dropdb","--if-exists",database)


if __name__=="__main__":
    main(Path(sys.argv[1]).resolve(),Path(sys.argv[2]).resolve())
