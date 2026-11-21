"""Exercise resident-specific knowledge through the normal journal, cue, and dialogue APIs."""
from datetime import datetime, timedelta, timezone
from uuid import uuid4
import json


def check_memories(api, beat, event, world, campaign, residents, legacy, calls):
    owner, listener, late = residents
    session = str(uuid4())
    prefix = f"saves/{campaign}/characters/"

    def view(actor, section):
        return api(prefix + actor + "/history/" + section)

    def entries(actor, section):
        return view(actor, section)["items"]

    def line(seq, speaker, to, audience, words, **extra):
        when=datetime.now(timezone.utc)-timedelta(minutes=3,seconds=10 if seq==2 else 0)
        item = {"session": session, "seq": seq, "speaker": speaker, "to": to, "audience": audience,
                "words": words, "at": when.isoformat(), **extra}
        ident = str(uuid4())
        message = {"world": dict(world), "kind": "group", "parent": world.get("cueHead"), "id": ident,
                   "people": [], "line": item}
        api("module/cue", message)
        api("module/cue", message)
        world["cueHead"] = ident
        beat(world["day"])
        return ident

    assert [x["text"] for x in view(owner, "sessions/" + legacy)] == ["I promise to deliver flour tomorrow.", "Welcome back."]
    assert all(x["kind"] == "private" for x in entries(owner, "sessions"))
    api(prefix + listener + "/history/sessions/" + legacy, expected=404)
    event(17, "work", "Worked at the mill before the rain.")
    first = line(1, owner, "group", [owner, listener], "I worked at the mill before the rain.")
    second = line(2, listener, owner, [owner, listener, late], "Will you bring flour tomorrow?")
    third = line(3, owner, listener, [owner, listener, late], "Yes, I will.",
                 promise="Yes, I will.", proposal={"source":second,"quote":"Will you bring flour tomorrow?"})
    last = line(4, "player", late, [owner, late], "I found a blue feather outside.")

    def visible(actor):
        return [x["source"] for x in view(actor, "sessions/" + session)]

    assert visible(owner) == [first, second, third, last]
    assert visible(listener) == [first, second, third]
    assert visible(late) == [second, third, last]
    assert [x for x in entries(late, "sessions") if x["id"] == session][0]["entries"] == 3
    assert "Worked at the mill" in str(entries(owner, "daily"))
    assert not entries(listener, "daily") and not entries(late, "daily")
    for resident in residents:
        promises = [x for x in entries(resident, "commitments") if x.get("sourceLine") == third]
        assert len(promises) == 1 and promises[0]["speakerLabel"] == "Miller" and promises[0]["status"] == "open"
        assert promises[0]["proposalLine"] == second and promises[0]["proposal"] == "Will you bring flour tomorrow?" and promises[0]["due"] == 18
        assert not [row for day in entries(resident, "daily") for row in view(resident, "daily/" + day["id"]) if row["source"] in (first, second, third, last)]
    # Loading the earlier branch hides later speech for everyone and restores the promise state.
    world["cueHead"] = first
    beat(17)
    assert visible(listener) == [first]
    api(prefix + late + "/history/sessions/" + session, expected=404)
    assert all(x.get("sourceLine") != third for x in entries(owner, "commitments"))
    for resident, expected in [(listener, first), (late, None)]:
        response = api("dialogue/group", {"campaign":campaign, "session":session, "head":first,
                                         "actor":resident, "audience":[owner, resident]})
        prompt = json.loads(calls[-1]["messages"][1]["content"])
        assert response.get("previous") == expected and prompt["playerName"] == "Test player"
        assert [x["id"] for x in prompt["heardInOrder"]] == ([first] if expected else [])
        assert not prompt["yourMemory"]["daily"]
        assert all(x["mode"] == "group" for x in prompt["yourMemory"].get("heardSpeech", []))
        if resident == listener:
            assert all(not x["uniqueNative"] for x in response["residents"])
    api("dialogue/group", {"campaign":campaign,"session":session,"head":str(uuid4()),"actor":owner,"audience":[owner,late]}, expected=425)
    world["cueHead"] = last
    beat(17)
    for days in [1, 7]:
        api("options", {"provider":"openai_compatible","dailyDays":days,"replyTokens":80}, "PUT", 204)
        assert "Worked at the mill" in str(view(owner, "daily/17"))
        assert visible(late) == [second, third, last]
    # An alternate future can contain the same short agreement with independent fulfillment evidence.
    promise=next(x for x in entries(owner,"commitments") if x.get("sourceLine")==third)
    api("commitments/"+promise["id"]+"/fulfill", {"campaign":campaign,"actor":owner}, expected=204)
    assert next(x for x in entries(owner,"commitments") if x["id"]==promise["id"])["status"]=="fulfilled"
    world["cueHead"]=third;beat(17)
    assert next(x for x in entries(owner,"commitments") if x["id"]==promise["id"])["status"]=="open"
    play=str(uuid4())
    visits=[str(uuid4()),str(uuid4())]
    for visit,question in [(visits[0],"Do you still keep bees?"),(visits[0],"Do you still keep the honey?"),(visits[1],"Do you still keep a garden?")]:
        actor_turn={"id":str(uuid4()),"campaign":campaign,"actor":owner,"text":question,
                    "context":{"session":play,"conversation":visit,"head":world["cueHead"]}}
        assert api("dialogue/prepare",actor_turn)["state"]=="ready"
        api("module/cue",{"id":actor_turn["id"],"world":dict(world),"kind":"talk","people":[],"parent":world["cueHead"]})
        api("dialogue/finish",dict(actor_turn,reaction="neutral"))
        world["cueHead"]=actor_turn["id"];beat(17)
    assert [len(view(owner,"sessions/"+visit)) for visit in visits]==[4,2]
    api(prefix+listener+"/history/sessions/"+visits[0],expected=404)
    for resident in [owner, listener, late]:
        api("dialogue",{"campaign":campaign,"actor":resident,"id":str(uuid4()),"text":"What do you know about the mill and the rain?"})
        context=json.loads(calls[-1]["messages"][0]["content"].split("\nRecall: ",1)[1])
        assert any("Worked at the mill" in row["description"] for row in context["daily"])==(resident==owner)
        accounts=[row for row in context.get("heardSpeech",[]) if row["source"]==first]
        assert bool(accounts)==(resident in (owner,listener))
        if accounts:assert accounts[0]["speaker"]=="Miller" and accounts[0]["mode"]=="group"
    api("dialogue",{"campaign":campaign,"actor":listener,"id":str(uuid4()),"text":"Did someone tell you about keeping bees?"})
    context=json.loads(calls[-1]["messages"][0]["content"].split("\nRecall: ",1)[1])
    assert "Do you still keep bees?" not in str(context)
    beat(18)
    latest=line(4,"player",owner,[owner,listener],"What did you do yesterday?")
    for days,section in [(1,"olderActivity"),(7,"daily")]:
        api("options",{"provider":"openai_compatible","dailyDays":days,"replyTokens":80},"PUT",204)
        api("dialogue/group",{"campaign":campaign,"session":session,"head":latest,"actor":owner,"audience":[owner,listener]})
        memory=json.loads(calls[-1]["messages"][1]["content"])["yourMemory"]
        assert "Worked at the mill before the rain." in str(memory[section]) and "source" in str(memory[section])
    print("PASS two private visits in one game day, an accepted proposal with its source, independent fulfillment on a saved branch, and dated firsthand recall at both window settings.",flush=True)
    print("PASS separate daily and conversation journals, legacy private lines, exact group attribution, late arrival, departure, scoped recall, duplicated native IDs, replay, and save rewind.", flush=True)
    return session
