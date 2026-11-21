"""Run in ReignServer WSL as root. Uses the installed game, the signed-in Windows Codex host and a disposable database."""
import concurrent.futures
import base64
import json
import os
from pathlib import Path
import pwd
import shutil
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import uuid
import xml.etree.ElementTree as ET


def request(path, data=None, method=None):
    body = json.dumps(data).encode() if data is not None else None
    req = Request("http://127.0.0.1:17864/api/" + path, data=body, method=method,
                  headers={"Content-Type": "application/json"})
    with urlopen(req, timeout=95) as response:
        return json.loads(response.read() or "null")


def windows(path):
    return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()


def postgres(*args):
    return subprocess.check_output(["runuser", "-u", "postgres", "--", *args], text=True).strip()


def main(package, output, mode="Relationships"):
    if mode not in ("Relationships", "Quests", "Interactions", "Walkthrough"):
        raise ValueError("Unknown isolated game suite")
    app = package.resolve() / "app"
    output.mkdir(parents=True, exist_ok=True)
    game_report = (output / "game.json").resolve()
    token = uuid.uuid4().hex
    database = "echoes_talk_" + token
    account = pwd.getpwnam("mirthwood_echoes")
    with tempfile.TemporaryDirectory(prefix="echoes-talk-") as name:
        tmp = Path(name)
        tmp.chmod(0o755)
        state, saves = tmp / "state", tmp / "saves"
        state.mkdir(); saves.mkdir()
        os.chown(state, account.pw_uid, account.pw_gid)
        # Match the Windows account and native save selected by Test-Game.ps1.
        windows_profile = subprocess.check_output([
            "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe", "-NoProfile", "-NonInteractive",
            "-Command", "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Environment]::GetFolderPath('UserProfile')"
        ], encoding="utf-8").strip()
        profile = Path(subprocess.check_output(["wslpath", "-u", windows_profile], text=True).strip())
        save = profile / "AppData/LocalLow/Bad Ridge Games/Mirthwood/SaveData/Slot0.savegame"
        shutil.copyfile(save, saves / save.name)
        native_data = Path("/mnt/c/SteamLibrary/steamapps/common/Mirthwood/Mirthwood_Data")
        game_data = tmp / "native-profile" / "Mirthwood_Data"
        (game_data / "Managed").mkdir(parents=True)
        for relative in ("Managed/Assembly-CSharp.dll", "resources.assets"):
            (game_data / relative).symlink_to((native_data / relative).resolve(strict=True))
        env = dict(os.environ, MIRTHWOOD_ECHOES_DB="Host=/var/run/postgresql;Username=mirthwood_echoes;Database=" + database,
                   MIRTHWOOD_ECHOES_STATE=str(state), MIRTHWOOD_ECHOES_GAME=str(game_data.parent),
                   MIRTHWOOD_ECHOES_SAVES=str(saves), MIRTHWOOD_ECHOES_PORT="17864", MIRTHWOOD_ECHOES_HARNESS_ID=token,
                   HOME=str(state), DOTNET_PROCESSOR_COUNT="2", Logging__LogLevel__Default="Warning", WSL_INTEROP="/run/WSL/1_interop")
        postgres("createdb", "-O", "mirthwood_echoes", database)
        log_path = tmp / "server.log"
        with log_path.open("w") as log:
            def start():
                p = subprocess.Popen(["/usr/bin/dotnet", str(app / "Mirthwood.Echoes.Server.dll")],
                                     cwd=app, user=account.pw_uid, group=account.pw_gid, extra_groups=[], env=env, stdout=log, stderr=log)
                for _ in range(80):
                    try:
                        if request("test-session")["run"] == token and request("saves"):
                            return p
                    except OSError:
                        pass
                    if p.poll() is not None: break
                    time.sleep(.2)
                p.terminate(); p.wait()
                raise RuntimeError("The disposable backend did not start. "+log_path.read_text()[-1200:])
            p = None
            try:
                p = start()
                request("providers/codex_subscription", {"defaultModel":"gpt-6.2-sol","conversation":"gpt-6.1-terra",
                        "conversationEffort":"medium","codexHost":"windows"}, "PUT")
                request("options", {"provider":"codex_subscription","replyTokens":72,"dailyDays":2}, "PUT")
                def literal(value): return "'"+value.replace("'","''")+"'"
                script="$env:PSModulePath=Join-Path $PSHOME 'Modules'; $ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; & "
                if mode=="Walkthrough":
                    script+=literal(windows(Path("tests/native_talk/Measure-Talk.ps1").resolve()))+" -Mode Walkthrough -TalkTestRun "+token+" -ReportDirectory "+literal(windows(output.resolve()))
                else:
                    script+=literal(windows(Path("scripts/Test-Game.ps1").resolve()))+" -Mode "+mode+" -IsolatedSaveSlot 0 -TalkTestRun "+token+" -Report "+literal(windows(game_report))+" -TimeoutSeconds 470"
                game_command=["/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass",
                              "-OutputFormat","Text","-EncodedCommand",base64.b64encode(script.encode("utf-16-le")).decode()]
                if mode == "Quests":
                    # Windows Local and LocalLow share the same AppData directory.
                    app_data=next(parent for parent in save.parents if parent.name=="AppData")
                    harness=app_data/"Local"/"MirthwoodEchoes"/"Harness"/token
                    offline = online = False
                    with subprocess.Popen(game_command,stderr=subprocess.PIPE) as process:
                        while process.poll() is None:
                            for phase in ("offline","online"):
                                signal=harness/(phase+".request")
                                if not signal.is_file() or signal.read_text()!=token:
                                    continue
                                if phase=="offline" and not offline:
                                    p.terminate();p.wait(timeout=25)
                                    offline=True
                                elif phase=="online" and offline and not online:
                                    p=start()
                                    online=True
                                else:
                                    continue
                                acknowledgement=harness/(phase+".ready")
                                temporary=acknowledgement.with_suffix(".tmp")
                                temporary.write_text(token);temporary.replace(acknowledgement)
                            time.sleep(.15)
                        _,stderr=process.communicate()
                        game_run=subprocess.CompletedProcess(game_command,process.returncode,stderr=stderr)
                else:
                    game_run=subprocess.run(game_command,stderr=subprocess.PIPE)
                if game_run.returncode:
                    detail=game_run.stderr.decode("utf-8",errors="replace").strip()
                    if detail.startswith("#< CLIXML"):
                        try:
                            xml=ET.fromstring(detail.partition("\n")[2])
                            detail=" ".join(e.text or "" for e in xml if e.attrib.get("S")=="Error").replace("_x000D__x000A_"," ")
                        except ET.ParseError:
                            if game_report.exists(): detail=str(json.loads(game_report.read_text(encoding="utf-8-sig"))[0]["result"].get("error") or "See game.json for the failed checks.")
                    raise RuntimeError("Isolated game check failed. "+detail[:350])
                if mode == "Walkthrough":
                    observed=json.loads((output/"input.json").read_text(encoding="utf-8-sig"))
                    assert observed["stage"]=="finished" and observed["draftPaused"] and observed["nativeReload"] and "Talk" in observed["menu"],observed
                    p.terminate(); p.wait(timeout=35); p=start()
                    rows=json.loads(postgres("psql","-d",database,"-Atc","""
                        SELECT COALESCE(json_agg(t ORDER BY t.started),'[]') FROM (
                          SELECT t.id,t.campaign,t.actor,a.name AS resident,t.player_text AS text,t.reply,t.state,t.reaction,
                                 t.talk->>'topic' AS topic,t.talk->'handoff' AS handoff,t.cue_binding AS context,t.started
                          FROM turns t JOIN actors a ON (a.campaign,a.key)=(t.campaign,t.actor)
                        ) t"""))
                    assert rows and all(t["state"]=="complete" and t["text"] and t["reply"] for t in rows),rows
                    last=rows[-1]
                    expected_bubble=last["reply"]
                    assert last["resident"]==observed["resident"] and expected_bubble==observed["bubbleSource"],last
                    for t in rows:
                        cached=request("dialogue/prepare",{k:t[k] for k in ("id","campaign","actor","text","context")})
                        assert cached["state"]=="complete" and cached["reply"]==t["reply"] and cached.get("handoff")==t["handoff"],t
                    calls=log_path.read_text().count("Echoes Codex turn accepted: model=gpt-6.1-terra effort=medium")
                    assert calls==len(rows),(calls,len(rows))
                    result={"run":token,"turns":rows,"calls":calls,"recoveredAfterRestart":True}
                    (output/"server.json").write_text(json.dumps(result,indent=2)+"\n")
                    if source:=os.getenv("MIRTHWOOD_ECHOES_TONE_SOURCE"):
                        cases=json.loads(Path(source).read_text(encoding="utf-8-sig"))[0]["result"]["impressionDialogue"]
                        friend=next(t for t in cases if t["scenario"]=="knownParagon")
                        infamous=next(t for t in cases if t["scenario"]=="introducedInfamous")["observation"]
                        words="If I needed someone to vouch for my character at the market, would you put your name beside mine?"
                        tone={"resident":friend["resident"],"text":words,"samples":{}}
                        for label,standing in (("well-regarded",friend["observation"]),("ill-regarded",infamous)):
                            impression=dict(friend["observation"],standing=standing["standing"],title=standing["title"])
                            answer=request("dialogue/prepare",{"id":str(uuid.uuid4()),"campaign":friend["campaign"],"actor":friend["actor"],
                                           "text":words,"socialDesire":.5,"playerImpression":impression})
                            assert answer["state"]=="ready" and set(answer["choices"])=={"warm","neutral","cool"},answer
                            tone["samples"][label]={"impression":impression,"topic":answer["topic"],"choices":answer["choices"]}
                        # Both alternatives use the same journal. Neither is finished or allowed to enter the other's recall.
                        (output/"trust-comparison.json").write_text(json.dumps(tone,indent=2)+"\n")
                    print(json.dumps(result),flush=True)
                    return
                game = json.loads(game_report.read_text(encoding="utf-8-sig"))[0]["result"]
                if mode == "Interactions":
                    from native_interactions import game_proof
                    proof=game_proof(game,request,lambda sql: postgres("psql","-d",database,"-Atc",sql),log_path)
                    (output/"server.json").write_text(json.dumps(proof,indent=2)+"\n")
                    print(json.dumps(proof),flush=True)
                    return
                if mode == "Quests":
                    p.terminate(); p.wait(timeout=35); p=start()
                    from quest_dialogue import verify
                    proof=verify(game, request, lambda sql: postgres("psql","-d",database,"-Atc",sql), log_path)
                    (output/"server.json").write_text(json.dumps(proof,indent=2)+"\n")
                    print(json.dumps(proof),flush=True)
                    return
                trials = game["trials"]
                assert game["playerImpressions"]["passed"], game["playerImpressions"]
                expected=["Leisure","Intellectual","Small Talk","Small Talk","None","None","Deep"]
                assert [t["topic"] for t in trials]==expected and all(t["passed"] for t in trials)
                departing=[t for t in trials if (t["handoff"] or {}).get("kind")=="ended"]
                assert {t["branch"] for t in departing}>={"warm","cool"}
                assert all(t["firstBubbleMatched"] and t["endingReply"]!=t["continuingReply"] for t in trials)
                assert all(t["spoken"]==t["endingReply"] and t["endInputClosed"] and t["exitHintFits"] for t in departing)
                assert all(t["spoken"]==t["continuingReply"] for t in trials if t not in departing)
                sessions=game["balance"]
                assert len(sessions)==4 and all(s["matched"] and s["echoes"]==s["stock"] and s["recoveredTo"]>s["recoveredFrom"] for s in sessions)
                assert len(sessions[0]["stock"])>=3 and len(sessions[1]["stock"])==len(sessions[3]["stock"])==1
                assert all(s["stock"][-1]["handoff"]=="ended" for s in sessions)
                rested=game["afterRest"]
                assert rested["passed"] and rested["first"]["kind"]=="ended" and rested["second"]["kind"]=="followup" and rested["debits"]==1
                scale=game["desireScale"];assert scale["passed"] and len(scale["scaled"])>scale["stockTurns"]
                usual=sessions[0]["stock"][0]["desireBefore"]-sessions[0]["stock"][0]["desireAfter"]
                story=game["story"]
                assert game["freshConversation"] and len(story)==2 and story[0]["previousRun"]==0 and abs(story[1]["previousRun"]-story[0]["positiveRun"])<.00002
                assert all(t["passed"] and t["topic"]=="Deep" and abs(t["cost"]-usual*(1-.75*t["positiveRun"]))<.00002 for t in story)
                assert {s["echoes"][0]["topic"] for s in game["cardBalance"]}=={"Rumors","Cheer Up","Complain"}
                assert all(s["matched"] for s in game["cardBalance"])
                impression_turns=game["impressionDialogue"]
                outfits=game["playerImpressions"]
                assert len(impression_turns)==6 and all(t["passed"] for t in impression_turns)
                views=json.loads(postgres("psql","-d",database,"-Atc",
                    "SELECT json_object_agg(id,talk->'playerImpression') FROM turns"))
                for case in impression_turns:
                    scenario=case["scenario"]
                    sight=case["observation"]
                    view=views[case["id"]]
                    assert view["visibleClothing"]==sight["visibleClothing"] and view["relationshipToPlayer"]==sight["familiarity"],case
                    if scenario=="anonymousArmor":
                        assert "knownPlayer" not in view and "onlyIfIntroducingThemself" not in view
                        assert view["visibleClothing"]==outfits["first"]["visibleClothing"]
                        assert sight["name"].casefold() not in case["reply"].casefold()
                    elif scenario.startswith("introduced"):
                        identity=view["onlyIfIntroducingThemself"]
                        assert "knownPlayer" not in view and identity["name"]==sight["name"]
                        if scenario=="introducedObscure":
                            assert identity=={"name":sight["name"],"nameRecognition":"obscure"}
                        else:
                            assert identity["nameRecognition"]=="famous" and identity["publicStanding"]=="ill-regarded"
                    else:
                        assert view["knownPlayer"]["nameRecognition"]=="famous" and view["knownPlayer"]["publicStanding"]=="well-regarded"
                        if scenario in ("vanityArmor","underlayers"):
                            assert view["visibleClothing"]==outfits["vanity" if scenario=="vanityArmor" else "hidden"]["visibleClothing"]
                all_turns=trials+story+impression_turns
                p.terminate(); p.wait(timeout=25); p=start()
                game_world=json.loads(postgres("psql","-d",database,"-Atc","SELECT json_build_object('slot',slot,'created',created,'player',player,'day',day) FROM campaigns ORDER BY slot LIMIT 1"))
                for trial in all_turns:
                    turn = {k:trial[k] for k in ("id","campaign","actor","text","context")}
                    with concurrent.futures.ThreadPoolExecutor(3) as pool:
                        reads=list(pool.map(lambda _:request("dialogue/prepare",turn),range(3)))
                    first=reads[0]
                    assert all(r==first for r in reads) and first["state"]=="complete" and first["reaction"]==trial["branch"] and first["topic"]==trial["topic"]
                    assert first.get("handoff")==trial["handoff"]
                    # The harness deliberately restored the pre-trial save. Inspect each recorded timeline independently.
                    request("module/heartbeat",{"version":"fixture","world":dict(game_world,cueHead=turn["id"])})
                    entries=request("saves/"+turn["campaign"]+"/characters/"+turn["actor"]+"/history/conversations")["items"]
                    entry=next(e for e in entries if e["id"]==turn["id"])
                    assert entry["reply"]==first["reply"]==trial.get("spoken",trial["reply"])
                    assert not entry.get("nativeHandoff")
                    assert request("dialogue/finish",dict(turn,reaction="unknown"))==first
                    for changed in [dict(turn,text="A different turn."),dict(turn,actor=next(t["actor"] for t in trials if t["actor"]!=turn["actor"]))]:
                        try: request("dialogue/prepare",changed)
                        except HTTPError as exc: assert exc.code==409
                        else: raise AssertionError("The server accepted a reused ID with different words or a different resident.")
                sql="""SELECT json_build_object('turns',count(*),'complete',count(*) FILTER(WHERE state='complete'),
                       'promises',(SELECT count(*) FROM commitments),'promiseText',(SELECT min(statement) FROM commitments),
                       'models',array_agg(distinct talk->>'model')) FROM turns"""
                counts=json.loads(postgres("psql","-d",database,"-Atc",sql))
                calls=log_path.read_text().count("Echoes Codex turn accepted: model=gpt-6.1-terra effort=medium")
                assert calls==counts["turns"]==counts["complete"]==len(all_turns) and counts["promises"]==1 and counts["promiseText"]=="I will bring flour to the mill tomorrow.",(calls,counts)
                proof=dict(counts,acceptedCodexTurns=calls,reasoning="medium",parallelCachedReads=3*len(all_turns),
                           replayAfterBackendRestart=True,conflictingFinishesKeptFirst=True,scopeCollisionsRejected=2*len(all_turns))
                proof["playerImpressions"]={t["scenario"]:views[t["id"]] for t in impression_turns}
                proof["timings"]=json.loads(postgres("psql","-d",database,"-Atc",
                    "SELECT json_agg(json_build_object('id',id,'preparedMs',first_ms,'journaledMs',total_ms) ORDER BY started) FROM turns"))
                if os.environ.get("MIRTHWOOD_ECHOES_CHECK_JOURNAL") == "1":
                    played=next(t for t in trials if t["text"].startswith("I will bring flour"))
                    request("module/heartbeat",{"version":"fixture","world":dict(game_world,cueHead=played["id"])})
                    node=shutil.which("node.exe") or "/mnt/c/Program Files/nodejs/node.exe"
                    subprocess.run([node,windows(Path("tests/journal_browser.cjs").resolve()),windows(output.resolve()),"--played-turn"],check=True,timeout=90)
                    proof["playedJournalVerified"]=True
                (output/"server.json").write_text(json.dumps(proof,indent=2)+"\n")
                print(json.dumps(proof),flush=True)
            finally:
                if p is not None and p.poll() is None: p.terminate(); p.wait(timeout=35)
                postgres("dropdb","--if-exists",database)


if __name__ == "__main__":
    main(Path(sys.argv[1]),Path(sys.argv[2]),*sys.argv[3:])
