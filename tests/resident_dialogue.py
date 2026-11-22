"""Windows: python tests/resident_dialogue.py (--interface-fixture | --live-averil | --live-topic-flow) <built server package>.

Runs the bundled module in Steam against a private WSL server and a copy of slot 0.
The interface test uses the local fixture. The Averil test uses the selected Windows Codex subscription.
Use --play PACKAGE RESIDENT [tired|eager] for an unscripted conversation and --say RUN "words" after each reply.
Use --group PACKAGE for the attendance regression or --play-group PACKAGE [eager|tired] to continue it in your own words.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time
from urllib.request import Request, urlopen
import uuid


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def api(path, data=None, method=None):
    body = None if data is None else json.dumps(data).encode()
    req = Request("http://127.0.0.1:17864/api/"+path, data=body, method=method,
                  headers={"Content-Type":"application/json"})
    with urlopen(req, timeout=85) as response:
        return json.loads(response.read() or b"null")


def free_port():
    with socket.socket() as check:
        if check.connect_ex(("127.0.0.1",17864)) == 0:
            raise RuntimeError("The isolated server port is in use.")


def serve(token, saves, package, output):
    import pwd
    import signal
    import tempfile
    from relationships import postgres
    work = Path(output).resolve(strict=True)
    app = (Path(package)/"app").resolve(strict=True)
    if work.name!=uuid.UUID(hex=token).hex or not (work/"game").is_dir() or not (Path(saves)/"Slot0.savegame").is_file():
        raise ValueError("Missing private game or save")
    free_port()
    account = pwd.getpwnam("mirthwood_echoes")
    database = "echoes_talk_"+token
    postgres("createdb","-O",account.pw_name,database)
    try:
        with tempfile.TemporaryDirectory(prefix="echoes-resident-") as temp:
            os.chown(temp,account.pw_uid,account.pw_gid)
            env = dict(os.environ,HOME=temp,WSL_INTEROP="/run/WSL/1_interop",DOTNET_PROCESSOR_COUNT="2",
                       MIRTHWOOD_ECHOES_DB="Host=/var/run/postgresql;Username=mirthwood_echoes;Database="+database,
                       MIRTHWOOD_ECHOES_PORT="17864",MIRTHWOOD_ECHOES_HARNESS_ID=token,MIRTHWOOD_ECHOES_STATE=temp,
                       MIRTHWOOD_ECHOES_GAME=str((work/"game").resolve()),MIRTHWOOD_ECHOES_SAVES=saves)
            with (work/"server.log").open("w") as log:
                process = subprocess.Popen(["/usr/bin/dotnet",str(app/"Mirthwood.Echoes.Server.dll")],cwd=app,env=env,
                                           user=account.pw_uid,group=account.pw_gid,extra_groups=[],stdout=log,stderr=log)
                try:
                    end=time.monotonic()+(2220 if (Path(saves).parent/"resident.txt").exists() else 720)
                    while process.poll() is None and not (work/"stop-server").exists() and time.monotonic()<end:
                        time.sleep(.3)
                    if process.poll() is not None: raise RuntimeError("Private server exited early")
                    query = """SELECT coalesce(json_agg(row_to_json(t)),'[]') FROM
                      (SELECT player_text,reply,state,talk->>'topic' AS topic,talk->'handoff' AS handoff,reaction,
                      talk->>'flow' AS flow,talk->'deliveredContinuation' AS delivered_continuation,
                      first_ms,total_ms FROM turns ORDER BY started) t"""
                    write(work/"turns.json",json.loads(postgres("psql","-XAt","-d",database,"-c",query)))
                finally:
                    if process.poll() is None:
                        process.send_signal(signal.SIGINT)
                        try: process.wait(timeout=15)
                        except subprocess.TimeoutExpired: process.kill();process.wait()
    finally:
        postgres("dropdb","--force",database)


def powershell(script):
    return subprocess.check_output(["powershell.exe","-NoProfile","-NonInteractive","-Command",script],text=True).strip()


def linux(path):
    path=path.resolve()
    return "/mnt/"+path.drive[0].lower()+"/"+path.as_posix().split(":/",1)[1]


def send_line(token, words):
    session=Path(os.environ["LOCALAPPDATA"])/"MirthwoodEchoes/Harness"/uuid.UUID(hex=token).hex
    if not (session/"resident.txt").is_file() or json.loads((session/"harness.json").read_text())["id"]!=token:
        raise ValueError("This is not an active private roleplay run.")
    ready=max(int(p.name.split("-")[1]) for p in session.glob("ready-*"))
    draft=session/f"draft-{ready}.txt"
    tmp=session/(uuid.uuid4().hex+".tmp")
    try:
        tmp.write_text(words,encoding="utf-8")
        tmp.rename(draft)  # On Windows a second submission cannot replace the first.
    finally:
        tmp.unlink(missing_ok=True)


def run(package, mode, resident=None, starting_desire=None, knowledge=False):
    import winreg
    token=uuid.uuid4().hex
    free_port()
    if powershell("@(Get-Process -Name Mirthwood -ErrorAction SilentlyContinue).Count")!="0":
        raise RuntimeError("A game is already running.")
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r"Software\Valve\Steam") as key:
        steam=winreg.QueryValueEx(key,"SteamExe")[0]
    real=Path(os.environ.get("MIRTHWOOD_ECHOES_STEAM_GAME",r"C:\SteamLibrary\steamapps\common\Mirthwood"))
    module=Path("BepInEx/plugins/MirthwoodEchoes/Mirthwood.Echoes.dll")
    original=Path(os.environ["USERPROFILE"])/"AppData/LocalLow/Bad Ridge Games/Mirthwood/SaveData"
    files=[p for p in (original/name for name in ("Slot0.savegame","Slot0.metadata","Slot0.savegame.b","Slot0.metadata.b")) if p.exists()]
    baseline={str(p):digest(p) for p in [*files,real/module]}
    work=Path(os.environ.get("MIRTHWOOD_ECHOES_TEST_OUTPUT",".work/resident-dialogue"))/token
    game=(work/"game").absolute();game.mkdir(parents=True)
    harness=Path(os.environ["LOCALAPPDATA"])/"MirthwoodEchoes/Harness"
    session=harness/token;saves=session/"SaveData";saves.mkdir(parents=True)
    for p in files:
        shutil.copyfile(p,saves/p.name)
        assert digest(saves/p.name)==baseline[str(p)]
    write(session/"harness.json",dict(id=token,sourceSlot=0,sha256=digest(saves/"Slot0.savegame"),talkPort=17864,privateDiagnostics=True))
    if knowledge:(session/"memory-clock-test").touch()
    if resident:
        (session/"resident.txt").write_text(resident,encoding="utf-8")
        if starting_desire:
            (session/"starting-desire.txt").write_text(starting_desire,encoding="utf-8")
    for rel in ("Mirthwood.exe","Mirthwood_Data/Managed/Assembly-CSharp.dll","Mirthwood_Data/resources.assets"):
        (game/rel).parent.mkdir(exist_ok=True,parents=True);shutil.copyfile(real/rel,game/rel)
    launch=["wsl","-d","ReignServer","-u","root","--cd",str(Path.cwd()),"--","python3","-B",
            "tests/resident_dialogue.py","--server",token,linux(saves),linux(package),linux(work)]
    server=subprocess.Popen(launch)
    launched=False
    def owned():
        return powershell("@(Get-CimInstance Win32_Process -Filter \"Name='Mirthwood.exe'\" | Where-Object {$_.CommandLine -like '*"+token+"*'}).Count")!="0"
    def session_ready():
        return api("test-session")["run"]==token and api("saves")
    print("Private",mode,"run",token,flush=True)
    try:
        end=time.monotonic()+60
        while time.monotonic()<end:
            if server.poll() is not None: raise RuntimeError("The private server exited before startup")
            try:
                if session_ready():break
            except OSError: pass
            time.sleep(.4)
        else: raise RuntimeError("The private server did not index the copied save")
        if mode not in ("interface","voices"):
            api("providers/codex_subscription",dict(defaultModel="gpt-6.1-terra",conversation="gpt-6.1-terra",
                                                    conversationEffort="medium",codexHost="windows"),"PUT")
            api("options",dict(provider="codex_subscription",replyTokens=96,dailyDays=2),"PUT")
        assert api("module/check",{})["status"]=="ready"
        assert digest(game/module)==digest(package/"app/bundle"/module)
        probe=api("diagnostics/probes",dict(mode=mode,run=token))
        subprocess.Popen([steam,"-applaunch","2272900","-echoes-harness",token,"--doorstop-target-assembly",
                          str(game/"BepInEx/core/BepInEx.Preloader.dll")])
        launched=True;last=None;end=time.monotonic()+(2080 if resident else 590)
        seen_ready=0;seen_reply=0
        while time.monotonic()<end:
            probe=api("diagnostics/probes/"+probe["id"])
            if resident:
                reply_file=session/f"reply-{seen_reply}.json"
                if reply_file.is_file():
                    row=json.loads(reply_file.read_text(encoding="utf-8-sig"))
                    print(f"{resident}: {row['reply']}",flush=True);seen_reply+=1
                if (session/f"ready-{seen_ready}").is_file():
                    if knowledge:
                        words=[
                            "I like fishing. Plum tart is my favorite food. We landed a speckled trout beneath the alder footbridge.",
                            "My favorite color is green. Do you have one you like to wear?",
                            "Which stretch of river would you recommend for angling again? I'd love to go back there.",
                            "I've got my rod out again. Remind me, whereabouts did we catch that one together?",
                            "I'm off to the baker. Do you remember which treat I like best?"
                        ]
                        if seen_ready:
                            first=json.loads((session/"reply-0.json").read_text(encoding="utf-8-sig"))
                            path=f"saves/{first['campaign']}/characters/{first['actor']}/history/"
                            until=time.monotonic()+50
                            while True:
                                page=api(path+"conversations")
                                full=page["items"]
                                found=any(x["id"]==first["id"] for x in full)
                                if found==(seen_ready<2) or time.monotonic()>=until:break
                                time.sleep(.4)
                            assert found==(seen_ready<2),(seen_ready,found)
                            if seen_ready==1:first_day=page["day"]
                            assert page["day"]==first_day+(0,0,1,29,30)[seen_ready],(seen_ready,page["day"],first_day)
                            sessions=api(path+"sessions")["items"]
                            if seen_ready>=2:
                                source_day=next(x["day"] for x in api(path+"knowledge")["items"] if x["value"].lower()=="plum tart")
                                recaps=[r for r in sessions if r["day"]==source_day and r.get("hasOriginal") is False]
                                assert bool(recaps)==(seen_ready<4),(seen_ready,sessions)
                            facts=api(path+"knowledge")["items"]
                            assert {"fishing","plum tart"}<={x["value"].lower() for x in facts},facts
                            if seen_ready>=2:assert "green" in {x["value"].lower() for x in facts},facts
                            write(work/f"memory-day-{seen_ready}.json",dict(ordinaryPresent=found,knowledge=facts,sessions=api(path+"sessions")["items"]))
                        send_line(token,words[seen_ready])
                    print(f"Ready for player line {seen_ready+1}.",flush=True);seen_ready+=1
            if probe["state"]!=last:
                last=probe["state"];print(last,flush=True)
            if last not in ("waiting","running"):break
            time.sleep(1)
        write(work/"probe.json",probe)
        for check in (probe["result"] or {}).get("checks",[]):print(check,flush=True)
        if probe["state"]!="passed":raise RuntimeError(mode+" check "+probe["state"])
        if mode in ("interface","voices"):return
        if mode=="grouptalk":
            result=probe["result"]
            attendance=result["attendance"]
            assert all((attendance["late"] in row["line"]["audience"])==
                       (attendance["joinedAfter"]<row["line"]["seq"]<=attendance["departedAfter"]) for row in result["groupLines"])
            for guest in result["guests"]:
                base="saves/"+result["campaign"]+"/characters/"+guest["actor"]+"/history/sessions"
                lines=api(base+"/"+result["groupSession"])
                expected=[row["line"]["words"] for row in result["groupLines"] if guest["actor"] in row["line"]["audience"]]
                assert [row["text"] for row in lines]==expected, (guest["name"],lines,expected)
                write(work/(guest["actor"]+"-transcript.json"),lines)
                history=api(base.replace("/sessions","/daily"))["items"]
                records=[entry for day in history for entry in api(base.replace("/sessions","/daily/")+day["id"])]
                flour=[item for item in records if "2 Flour" in item["text"]]
                assert bool(flour)==(guest["name"]=="Averil"), (guest["name"],flour)
            subprocess.run(["node","tests/journal_browser.cjs",str(work),"--group"],check=True,timeout=90)
            return
        result=probe["result"]
        first=result["turns"][0]
        path="saves/"+first["campaign"]+"/characters/"+first["actor"]+"/history/conversations"
        if knowledge:
            assert len(result["turns"])==5 and "plum" in result["turns"][-1]["reply"].lower(),result["turns"][-1]
            for answer in result["turns"][2:4]:
                assert any(word in answer["reply"].lower() for word in ("alder","footbridge","speckled")),answer
        expected={r["id"]:r["reply"] for r in (result["turns"][-1:] if knowledge else result["turns"])}
        for r in result["openers"]:
            if knowledge and r["id"]!=result["turns"][-1]["id"]:continue
            source=hashlib.md5((r["id"]+"/"+r["actor"]).encode(),usedforsecurity=False).hexdigest()
            expected[str(uuid.UUID(hex=source))]=r["line"]
        end=time.monotonic()+15
        while True:
            entries=[r for r in api(path)["items"] if r["id"] in expected]
            if {r["id"]:r["reply"] for r in entries}==expected:break
            if time.monotonic()>=end:raise AssertionError(("Journal differs from the in-game speech",expected,entries))
            time.sleep(.5)
        write(work/"journal.json",entries)
        if knowledge:
            subprocess.run(["node","tests/journal_browser.cjs",str(work),"--knowledge"],check=True,timeout=90)
            print("PASS Averil recalled the fishing place on days 2 and 30, and the favorite after its recap expired.",flush=True)
        if not resident:
            subprocess.run(["node","tests/journal_browser.cjs",str(work),"--averil"],check=True,timeout=90)
        log=(work/"server.log").read_text(encoding="utf-8",errors="replace")
        calls={kind:log.count("Echoes Codex "+kind+" accepted: model=gpt-6.1-terra effort=medium") for kind in ("turn","opener")}
        write(work/"calls.json",calls)
        assert calls=={"turn":len(result["turns"]),"opener":len(result["openers"])}, calls
    finally:
        if launched:
            (session/"quit.request").write_text(token)
            end=time.monotonic()+40
            while owned() and time.monotonic()<end:time.sleep(.7)
        (work/"stop-server").touch()
        server.wait(timeout=45)
        after={f:digest(Path(f)) for f in baseline}
        receipt=dict(run=token,before=baseline,after=after,unchanged=after==baseline,closed=not owned(),
                     databaseRemoved=server.returncode==0,module=digest(game/module) if (game/module).exists() else None)
        write(work/"isolation.json",receipt)
        if not all(receipt[k] for k in ("unchanged","closed","databaseRemoved")):raise RuntimeError("Isolation check failed")
        if (game/"BepInEx/LogOutput.log").exists():shutil.copyfile(game/"BepInEx/LogOutput.log",work/"game-module.log")
        pattern="latest-group-*.png" if mode=="grouptalk" else "latest-roleplay-*.png" if resident else "latest-averil-*.png"
        patterns=("latest-native-overhead.png","latest-player-draft.png","latest-interface.png","latest-full-*.png","latest-named-player-speech.png","latest-compact-*.png") if mode=="interface" else (pattern,)
        if mode=="voices":patterns=("voice-player.png","voice-resident.png")
        for match in patterns:
            for capture in (harness.parent/"Diagnostics").glob(match):
                if capture.stat().st_mtime>=(session/"harness.json").stat().st_mtime:shutil.copyfile(capture,work/capture.name)
        if session.resolve().parent==harness.resolve() and json.loads((session/"harness.json").read_text())["id"]==token:
            shutil.rmtree(session)
        print("Verified original saves and installed module.",flush=True)


if __name__=="__main__":
    modes={"--live-averil":"residentdialogue","--interface-fixture":"interface","--live-topic-flow":"dialogueflow","--group":"grouptalk","--voices":"voices"}
    if len(sys.argv)==3 and sys.argv[1] in modes:run(Path(sys.argv[2]).resolve(strict=True),modes[sys.argv[1]])
    elif len(sys.argv)==3 and sys.argv[1]=="--knowledge" and os.name=="nt":
        run(Path(sys.argv[2]).resolve(strict=True),"roleplay","Averil","eager",True)
    elif sys.argv[1:2]==["--play-group"] and os.name=="nt" and (len(sys.argv)==3 or len(sys.argv)==4 and sys.argv[3] in ("tired","eager")):
        run(Path(sys.argv[2]).resolve(strict=True),"grouptalk","Group",sys.argv[3] if len(sys.argv)==4 else None)
    elif sys.argv[1:2]==["--play"] and os.name=="nt" and (len(sys.argv)==4 or len(sys.argv)==5 and sys.argv[4] in ("tired","eager")):
        run(Path(sys.argv[2]).resolve(strict=True),"roleplay",*sys.argv[3:])
    elif len(sys.argv)==4 and sys.argv[1]=="--say" and os.name=="nt":send_line(sys.argv[2],sys.argv[3])
    elif len(sys.argv)==6 and sys.argv[1]=="--server" and os.name=="posix":serve(*sys.argv[2:])
    else:raise SystemExit(__doc__)
