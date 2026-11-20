"""Native interaction receipts using the real HTTP and PostgreSQL paths."""
import json
import uuid
from concurrent.futures import ThreadPoolExecutor
from threading import Barrier, Event
from urllib.error import HTTPError


def game_proof(game, request, sql, log):
    turns = game["turns"]
    assert turns and not game.get("probeError") and all(c["passed"] for c in game["cases"]), game.get("probeError")
    assert [(r["action"], r["change"], r["uninterrupted"]) for r in game["passingGestures"]] == [("Greet", 1, True), ("Jeer", -2, True)]
    assert game["quickMenuPages"][0] == ["Talk", "Greet", "Jeer", "Next"] and "ground" in game["walkAway"]
    assert [x["kind"] for x in game["openers"]] == ["gift", "collectionReply"]
    for opener in game["openers"]:
        turn = next(r for r in turns if r["context"].get("event") == opener["id"])
        assert turn["context"]["remark"]["text"] == opener["line"]
    for row in turns:
        turn = {k: row[k] for k in ("id", "campaign", "actor", "text", "context")}
        with ThreadPoolExecutor(3) as pool:
            replies = list(pool.map(lambda _: request("dialogue/prepare", turn), range(3)))
        assert len({json.dumps(r, sort_keys=True) for r in replies}) == 1
        assert replies[0]["state"] == "complete"
        changed = dict(turn, context=dict(turn["context"], session=str(uuid.uuid4())))
        try:
            request("dialogue/prepare", changed)
        except HTTPError as e:
            assert e.code == 409
        else:
            raise AssertionError("Rebound cue accepted")
        row["reply"] = replies[0]["reply"]
        row["topic"] = replies[0]["topic"]
        folded=row["reply"].casefold()
        if "other resident a gift" in row["text"]: assert "copper" not in folded, row
        if "pick up from your work" in row["text"]: assert "flour" in folded and ("two" in folded or "2" in folded), row
        if "you just warn" in row["text"]: assert "vermin" in folded or "rat" in folded or "plague" in folded, row
        if "rumors or gossip" in row["text"]: assert row["topic"]=="Rumors", row
        if "lift your spirits" in row["text"]: assert row["topic"]=="Cheer Up", row
        if "need to vent" in row["text"]: assert row["topic"]=="Complain", row
    calls = log.read_text().count("Echoes Codex turn accepted: model=gpt-6.1-terra effort=medium")
    assert calls == len(turns), (calls, len(turns))
    assert int(sql("SELECT count(*) FROM turns WHERE state='complete' AND cue_binding IS NOT NULL")) == len(turns)
    return {"model": "gpt-6.1-terra", "effort": "medium", "calls": calls, "turns": turns,
            "cases": game["cases"], "restoredHead": game.get("restoredHead"),
            "events": int(sql("SELECT count(*) FROM cue_events"))}


def check_context(api, model, say, world, east, west, checked):
    uid = lambda: str(uuid.uuid4())
    prepare = "/api/dialogue/prepare"
    def event(kind, desc=None, parent=None, id=None):
        return {"id": id or uid(), "world": world, "parent": parent, "kind": kind, "people": [] if desc is None else
                [{"owner": "East-NPC", "nativeId": "twin", "description": desc}]}
    def beat(head, day=None):
        api("/api/module/heartbeat", {"version": "fixture", "world": dict(world, cueHead=head,day=day or world["day"])})
    first = event("gift", "Received a red apple.")
    after = event("collection", "Collected six topazes.", first["id"])
    sibling = event("hire", "Hired for woodcutting.", first["id"])
    with ThreadPoolExecutor(4) as pool:
        list(pool.map(lambda _:api("/api/module/cue",first),range(4)))
    for ev in (after, sibling):
        api("/api/module/cue", ev)
    api("/api/module/cue", dict(first, kind="work"), status=409)
    api("/api/module/cue", event("gift","Missing parent.",uid()),status=425)
    unknown=uid()
    detached=event("gift","A new gift after restoring the save.",unknown)
    api("/api/module/cue",dict(detached,parentNotQueued=True))
    beat(detached["id"])
    assert "new gift" in json.dumps(api("/api/saves/"+say("")["campaign"]+"/characters/"+east+"/history/interactions"))
    api("/api/module/cue", event("talk",parent=detached["id"],id=unknown),status=409)
    api("/api/module/cue", event("talk",parent=first["id"],id=unknown))
    beat(after["id"])
    turn = dict(say("Did you get those six topazes?"), context={"session": uid(), "head": after["id"], "event": after["id"]})
    base = "/api/saves/"+turn["campaign"]+"/characters/"
    def entries(actor,view="interactions"):
        return json.dumps(api(base+actor+"/history/"+view))
    assert "six topazes" in entries(east) and "six topazes" not in entries(west)
    beat(sibling["id"])
    assert "six topazes" not in entries(east) and "woodcutting" in entries(east)
    beat(None)
    assert "red apple" not in entries(east)
    beat(after["id"])
    api(prepare,dict(turn,id=uid(),actor=west),status=409)
    api(prepare,dict(turn,id=uid(),context=dict(turn["context"],event=uid())),status=425)
    opener={"owner":"East-NPC","nativeId":"twin","kind":"collectionReply","text":"Here are your six topazes."}
    calls_before=len(model.calls)
    for receipt in (None, first["id"]):
        api(prepare,dict(turn,id=uid(),context=dict(turn["context"],event=receipt,remark=opener)),status=409)
    assert len(model.calls)==calls_before
    key = turn["text"]
    model.samples[key]=json.dumps({"topic":"Small Talk","warm":"I remember collecting them.","cool":"I collected them.",
        "neutral":"I collected them.","farewells": {"warm":"Farewell.","neutral":"Goodbye.","cool":"Goodbye."},"commitments":[]})
    model.mode="stream"
    started, release=Event(),Event()
    model.gates[key]=(started,release)
    calls=len(model.calls)
    with ThreadPoolExecutor(5) as pool:
        original=pool.submit(api,prepare,turn)
        try:
            assert started.wait(10)
            assert all(x["state"]=="pending" for x in pool.map(lambda _:api(prepare,turn),range(4)))
            api(prepare,dict(turn,context=dict(turn["context"],event=first["id"])),status=409)
        finally:
            release.set()
            del model.gates[key]
        assert original.result()["state"]=="ready"
    assert len(model.calls)==calls+1 and "six topazes" in model.calls[-1]["messages"][0]["content"]
    api("/api/module/cue",event("talk",parent=after["id"],id=turn["id"]))
    api("/api/dialogue/finish",dict(turn,reaction="neutral",context=dict(turn["context"],head=None)),status=409)
    api("/api/dialogue/finish",dict(turn,reaction="neutral"))
    beat(turn["id"])
    assert key in entries(east,"conversations")
    beat(sibling["id"],world["day"]+12)
    assert key not in entries(east,"conversations") and "six topazes" not in entries(east,"memories")
    beat(turn["id"],world["day"]+12)
    assert key in entries(east,"conversations") and "six topazes" in entries(east,"memories")
    promise="I promise to deliver two pears tomorrow."
    sample={"topic":"Small Talk","warm":"Thank you.","cool":"So you say.","neutral":"I understand.",
            "farewells":{"warm":"Be well.","neutral":"Farewell.","cool":"Goodbye."},
            "commitments":[{"speaker":"player","quote":promise}]}
    model.samples[promise]=json.dumps(sample)
    def make_promise(parent):
        beat(parent)
        t=dict(say(promise),context={"session":uid(),"head":parent})
        assert api(prepare,t)["state"]=="ready"
        api("/api/module/cue",event("talk",parent=parent,id=t["id"]))
        api("/api/dialogue/finish",dict(t,reaction="neutral"))
        beat(t["id"])
        return t["id"], next(x for x in api(base+east+"/commitments") if x["statement"]==promise)["id"]
    root,pledge=make_promise(first["id"])
    resolved=event("work","Saw the miller.",root)
    alternate=event("work","Went fishing.",root)
    for e in (resolved,alternate):api("/api/module/cue",e)
    beat(resolved["id"])
    url="/api/commitments/"+pledge+"/fulfill"
    api(url,{"campaign":turn["campaign"],"actor":east},status=204)
    assert all(x["id"]!=pledge for x in api(base+east+"/commitments"))
    beat(alternate["id"])
    assert any(x["id"]==pledge for x in api(base+east+"/commitments"))
    beat(sibling["id"])
    api(url,{"campaign":turn["campaign"],"actor":east},status=409)
    _,other_pledge=make_promise(sibling["id"])
    assert other_pledge!=pledge
    beat(None)
    simultaneous=dict(say("Can you recall the apple?"),context={"session":uid(),"head":first["id"],"event":first["id"]})
    model.samples[simultaneous["text"]]=json.dumps(dict(sample,commitments=[]))
    participants=Barrier(8)
    def submit_together(_):
        participants.wait(timeout=10)
        return api(prepare,simultaneous)["state"]
    calls=len(model.calls)
    with ThreadPoolExecutor(8) as pool:
        observed=list(pool.map(submit_together,range(8)))
    assert set(observed)<={"pending","ready"} and "ready" in observed and len(model.calls)==calls+1
    parent=first["id"]
    private=[]
    for kind,phrase in (("gift","A private blue vase."),("collection","Seven red apples from the harvest."),("letter","I sent a private spice cake.")):
        e=event(kind,phrase,parent)
        api("/api/module/cue",e)
        private.append(e)
        parent=e["id"]
    beat(parent)
    assert all(e["people"][0]["description"] in entries(east) for e in private)
    beat(sibling["id"])
    with ThreadPoolExecutor(3) as pool:
        list(pool.map(lambda e:api("/api/module/cue",e),reversed(private)))
    assert all(e["people"][0]["description"] not in entries(east) for e in private)
    assert "spice cake" not in entries(west)
    beat(None)
    checked("replayed gift, work and mail deliveries stay off an earlier divergent save")
    checked("same-day save forks preserve independent promises, reported fulfillment and compacted memory")
    checked("native context scopes private observations and Talk to the loaded save, rejects rebound receipts and dispatches once across concurrent retries")
