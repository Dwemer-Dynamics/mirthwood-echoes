"""Run in ReignServer WSL as root. Exercise character journals with synthetic saves and local inference."""
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
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
from foundation_smoke import fixture_save, free_port

class Writer(BaseHTTPRequestHandler):
    calls=[]
    def log_message(self,*args):pass
    def do_POST(self):
        packet=json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self.calls.append(packet)
        self.send_response(200);self.send_header("Content-Type","application/json");self.end_headers()
        player=packet["messages"][1]["content"]
        marks=[{"speaker":"player","quote":player}] if player=="I promise to deliver flour tomorrow." else []
        text="The miller learned the rhythm of the mill in Brightoak." if packet["model"]=="historian" else json.dumps({"reply":"Welcome back.","commitments":marks})
        self.wfile.write(json.dumps({"choices":[{"message":{"content":text}}]}).encode())

def main(package,game_assets):
    account=pwd.getpwnam("mirthwood_echoes")
    db="echoes_journal_test_"+uuid.uuid4().hex[:12]
    def pg(*args):return subprocess.check_output(["runuser","-u","postgres","--",*args],text=True).strip()
    port=free_port()
    def api(path,data=None,method=None,expected=200):
        request=urllib.request.Request("http://127.0.0.1:"+str(port)+"/api/"+path,
            data=json.dumps(data).encode() if data is not None else None,method=method,headers={"Content-Type":"application/json"})
        try:r=urllib.request.urlopen(request,timeout=15)
        except urllib.error.HTTPError as e:r=e
        with r:
            content=r.read();assert r.status==expected,(path,r.status,content[:450])
            if "ndjson" in r.headers.get("Content-Type",""):return [json.loads(x) for x in content.splitlines()]
            return json.loads(content) if content else None
    with tempfile.TemporaryDirectory(prefix="echoes-journal-") as loc,ThreadingHTTPServer(("127.0.0.1",0),Writer) as writer:
        tmp=Path(loc);tmp.chmod(0o755)
        state=tmp/"state";state.mkdir();os.chown(state,account.pw_uid,account.pw_gid)
        saves=tmp/"saves";saves.mkdir()
        game=tmp/"fake-game";(game/"Mirthwood_Data/Managed").mkdir(parents=True)
        for rel in ("resources.assets","Managed/Assembly-CSharp.dll"):
            (game/"Mirthwood_Data"/rel).symlink_to(game_assets/rel)
        created="2025-01-02T12:00:00";file=fixture_save(saves,0,created,day=10)
        second_created="2025-01-03T12:00:00"
        second=fixture_save(saves,1,second_created)
        later=json.loads(second.read_text())
        for component in later["saveData"]:
            if "DayNightCycle" in component["guid"]:
                component["data"]=json.dumps({"dayNumber":4,"yearNumber":1})
        second.write_text(json.dumps(later))
        data=json.loads(file.read_text())
        for row in data["saveData"]:
            if "NPCController" in row["guid"]:
                payload=json.loads(row["data"]);info=payload["characterInfo"]
                info.update(origin=0,Class=2,favoriteItem=13,favoriteItem2=2,leastFavoriteItem=9,
                            personality={"values":[.47,.745,.39,-.037,.593,-.5]})
                if row["guid"].startswith("West"):info["backstory"]="A backstory written in the save."
                payload["relationships"]={"characterIDs":["p"],"relationshipData":[{"friendship":25,"romance":15}]}
                row["data"]=json.dumps(payload)
        file.write_text(json.dumps(data))
        threading.Thread(target=writer.serve_forever,daemon=True).start()
        pg("createdb","-O",account.pw_name,db)
        env=dict(os.environ,HOME=str(state),DOTNET_PROCESSOR_COUNT="2",MIRTHWOOD_ECHOES_STATE=str(state),MIRTHWOOD_ECHOES_GAME=str(game),
                 MIRTHWOOD_ECHOES_SAVES=str(saves),MIRTHWOOD_ECHOES_DB="Host=/var/run/postgresql;Username=mirthwood_echoes;Database="+db,
                 MIRTHWOOD_ECHOES_PORT=str(port),Logging__LogLevel__Default="Warning")
        app=package/"app"
        with (tmp/"test.log").open("w+") as log:
            proc=subprocess.Popen(["dotnet",str(app/"Mirthwood.Echoes.Server.dll")],cwd=app,env=env,user=account.pw_uid,group=account.pw_gid,
                                  extra_groups=[],stdout=log,stderr=log)
            try:
                for _ in range(80):
                    try:
                        if len(api("saves"))==2:break
                    except OSError:pass
                    time.sleep(.15)
                saves_view=api("saves");campaign=saves_view[0]["id"];other=saves_view[1]["id"]
                assert saves_view[1]["day"] is None
                api("module/heartbeat",{"version":"test","world":{"slot":1,"created":second_created,"player":"p","day":107,"yearLength":100}})
                assert api("saves")[1]["day"]==104
                print("PASS a live native year length resolves the previously unknown offline save date without rewriting the save.")
                prefix="saves/"+campaign+"/characters";roster=api(prefix)
                a,b=[next(x["key"] for x in roster if x["owner"]==owner) for owner in ("East-NPC","West-NPC")]
                left,right=prefix+"/"+a,prefix+"/"+b
                prof=api(left+"/profile")["profile"]
                assert [x["reading"] for x in prof["axes"]]==["Leans calm","Strongly genial","Leans cautious","Balanced","Leans passive"]
                assert prof["socialNeed"]=="Low" and prof["liked"]==["Light Ale","Custard"] and "Vintage" in prof["disliked"]
                assert "Small Talk" in prof["topicFit"]["closer"] and "Leisure" in prof["topicFit"]["farther"]
                assert prof["links"][0]["friendshipStatus"]=="Friend" and prof["links"][0]["romanceStatus"]=="Neutral"
                assert api(right+"/backstory",{})["text"]=="A backstory written in the save." and len(Writer.calls)==0
                api(left+"/backstory",{},expected=409);assert api(left+"/profile")["backstory"] is None
                api("providers/openai_compatible",{"defaultModel":"everyday","memory":"historian","endpoint":f"http://127.0.0.1:{writer.server_port}/v1/chat/completions"},"PUT",204)
                api("options",{"provider":"openai_compatible","dailyDays":2,"replyTokens":80},"PUT",204)
                written=api(left+"/backstory",{});again=api(left+"/backstory",{})
                assert written==again and written["model"]=="historian" and len(Writer.calls)==1
                facts=json.loads(Writer.calls[0]["messages"][1]["content"]);assert facts["town"]=="Brightoak" and facts["origin"]=="Turien" and facts["job"]=="Baker"
                print("PASS readable native temperament, signed axes, changing social need and class-specific gifts; grounded, missing-only backstory and memory override.",flush=True)
                world={"slot":0,"created":created,"player":"p","day":10,"yearLength":100}
                def beat(day):world["day"]=day;api("module/heartbeat",{"version":"test","world":world})
                def event(day,kind,text,action=None):
                    beat(day);event_id=str(uuid.uuid4())
                    m={"id":event_id,"world":world,"owner":"East-NPC","nativeId":"twin","kind":kind,"description":text,"chosenAction":action}
                    api("module/event",m);api("module/event",m);return event_id
                for day in (7,8,9):event(day,"social","Chose Small Talk with another resident.","Small Talk")
                event(10,"work","Collected two sacks of barley.")
                turn=str(uuid.uuid4());beat(10)
                msg={"campaign":campaign,"actor":a,"id":turn,"text":"I promise to deliver flour tomorrow.","liveTopicFit":{"closer":["Leisure"],"farther":["Deep"]}}
                assert api("dialogue",msg)[-1]["type"]=="done";assert api("dialogue",msg)[-1]["cached"]
                assert "AI-generated backstory" in Writer.calls[-1]["messages"][0]["content"] and "currentTopicFitIncludingMood" in Writer.calls[-1]["messages"][0]["content"]
                mark={"id":turn,"sourceType":"turn","axis":1,"direction":1,"reason":"The player made a personal promise."}
                api(right+"/personality",mark,expected=404);api(left+"/personality",mark,expected=204);api(left+"/personality",mark,expected=409)
                dynamics=api(left+"/personality");assert len(dynamics["changes"])==2 and dynamics["daysTogether"]==1
                assert not api(right+"/personality")["changes"]
                beat(8);assert not api(left+"/personality")["changes"];assert not api(left+"/history/conversations")["items"]
                beat(14);mem=api(left+"/history/memories")["items"]
                assert len(mem)==5 and any(r["kind"]=="conversation" and turn in r["sources"] and "flour" in r["body"] for r in mem)
                assert api(left+"/history/memories")["items"]==mem and not api(right+"/history/memories")["items"]
                assert not api("saves/"+other+"/characters/"+a+"/history/interactions")["items"]
                calls=len(Writer.calls);assert api(left+"/history/conversations")["items"][0]["reply"]=="Welcome back." and calls==len(Writer.calls)
                promise=api(left+"/history/commitments")["items"][0];assert promise["due"]==11
                api("commitments/"+promise["id"]+"/fulfill",{"campaign":campaign,"actor":a},expected=204)
                assert api(left+"/history/commitments")["items"][0]["status"]=="fulfilled";beat(13)
                assert api(left+"/history/commitments")["items"][0]["status"]=="open"
                print("PASS timelines, attribution, lasting promises, deterministic compaction, automatic and marked personality development, duplicate delivery, rewind and actor/campaign isolation.",flush=True)
                work_ids={event(13,"work","Hired-work fixture "+str(i)+".") for i in range(34)}
                page=api(left+"/history/interactions");assert len(page["items"])==30 and page["next"]
                from urllib.parse import quote
                more=api(left+"/history/interactions?before="+quote(page["next"]))
                assert len({v["id"] for v in page["items"]+more["items"]})==len(page["items"])+len(more["items"])
                api(right+"/history/interactions?before="+quote(page["next"]),expected=400)
                beat(17)
                summaries=[r for r in api(left+"/history/memories")["items"] if r["day"]==13]
                sources=[ident for summary in summaries for ident in summary["sources"]]
                assert len(summaries)==12 and len(sources)==len(work_ids) and set(sources)==work_ids
                assert all(1<=s["entries"]==len(s["sources"])<=3 for s in summaries)
                assert api(left+"/sources/turn/"+turn)["player"]==msg["text"]
                assert api(left+"/sources/event/"+sources[0])["description"].startswith("Hired-work fixture")
                api(right+"/sources/turn/"+turn,expected=404);beat(8);api(left+"/sources/turn/"+turn,expected=404)
                beat(17);api("dialogue",{"campaign":campaign,"actor":a,"text":"We have a deal.","commitment":True,"id":str(uuid.uuid4())})
                assert all(x["statement"]!="We have a deal." for x in api(left+"/history/commitments")["items"])
                print("PASS every source on a busy day survives compaction, full source text remains actor/day scoped, and client flags cannot create a promise.")
                # A controller reused by a different native ID must start a separate journal.
                for r in data["saveData"]:
                    if r["guid"].startswith("East-NPC-NPCController"):
                        v=json.loads(r["data"]);v["characterInfo"]["id"]="new-resident";r["data"]=json.dumps(v)
                file.write_text(json.dumps(data));api("saves/refresh",{})
                new=next(x for x in api(prefix) if x["owner"]=="East-NPC")
                assert new["key"]!=a and not api(prefix+"/"+new["key"]+"/history/conversations")["items"]
                print("PASS keyset pagination and controller reuse by a new resident.")
            except BaseException:
                log.flush();log.seek(0);print(log.read()[-2200:]);raise
            finally:
                proc.terminate()
                try:proc.wait(timeout=8)
                except subprocess.TimeoutExpired:proc.kill();proc.wait()
                writer.shutdown();pg("dropdb","--if-exists",db)

if __name__=="__main__":
    main(Path(sys.argv[1]).resolve(),Path(sys.argv[2]).resolve())

