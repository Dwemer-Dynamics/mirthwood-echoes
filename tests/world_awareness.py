"""Exercise the installed server's real prompt builders with two same-ID residents and a local model."""
import json
from copy import deepcopy
from datetime import datetime, timezone
from uuid import uuid4


def check_world_awareness(api, model, campaign, speaker, twin, checked):
    def uid():
        return str(uuid4())

    load = uid()
    world = dict(slot=1, created="2025-01-03T12:00:00", player="p", day=12, memorySession=load, cueHead=None)
    day = dict(season="Spring", seasonIndex=0, dayOfSeason=14, year=1, timeOfDay="Morning")
    joust = dict(title="Grand Joust", status="posted", source="public notice", place="Brightoak",
                 season="Spring", seasonIndex=0, dayOfSeason=14, year=1)
    market = dict(title="Traveling merchant", status="active", source="firsthand", when="earlier today")
    next_notice = dict(joust, title="Festival of Blossoms", dayOfSeason=21)
    later = dict(joust, season="Summer", seasonIndex=1, dayOfSeason=10)
    previous = dict(joust, season="Winter", seasonIndex=3, year=0, dayOfSeason=10, title="Yuletide")
    facts = dict(owner="East-NPC", nativeId="twin", world=world, day=12, calendar=day,
                 place=dict(name="Brightoak", region="Murkwater Marsh", interior=False),
                 nearbyPeople=["Anna"], bystandersPresent=True, sights=["the arena gate"], weather="Rain",
                 landmarks=[dict(name="Arena gate", direction="east")], interruptedActivity="tending a field",
                 today=[joust, market, dict(joust, title="Another posted gathering"),
                        dict(joust, title="Wrong day", dayOfSeason=13),
                        dict(market, title="Hidden quest", source="quest list"),
                        dict(joust, title="Unannounced random raid", status="scheduled")],
                 nextNotice=next_notice, previousNotice=previous, agentInventory="sealed private letter")
    answer = dict(warm="I hear you.", neutral="I hear you.", cool="I hear you.",
                  topic="Deep", commitments=[], flow="follow")
    model.mode = "stream"
    model.samples["group speaker"] = json.dumps(dict(words="Good morning.", to="player", ownTopic="Small Talk"))
    model.samples["passing speaker"] = "Good morning."
    model.samples["quest/opening"] = json.dumps(dict(intent="offer", evidence="", reply="Will you help?"))
    words = "My mother passed away. Could you sit with me?"
    model.samples[words] = json.dumps(answer)
    private = "/api/dialogue/prepare"

    def beat():
        api("/api/module/heartbeat", dict(version="fixture", hooks=2, dropped=0, world=world))

    def observation(kind, text):
        ident = uid()
        api("/api/module/cue", dict(id=ident, parent=world["cueHead"], kind=kind, world=world,
            people=[dict(owner="East-NPC", nativeId="twin", description=text)]))
        world["cueHead"] = ident

    def turn(scene, actor=speaker, text=words):
        return dict(id=uid(), campaign=campaign, actor=actor, text=text,
                    context=dict(session=world["memorySession"], head=world["cueHead"]), worldAwareness=scene)

    def private_recall():
        return json.loads(model.calls[-1]["messages"][0]["content"].split("\nRecall: ", 1)[1])

    def context():
        return json.loads(model.calls[-1]["messages"][1]["content"])

    def verify(scene):
        assert scene["day"] == 12 and scene["calendar"]["timeOfDay"] == "Morning", scene
        assert [e["title"] for e in scene["today"]] == ["Grand Joust", "Traveling merchant", "Another posted gathering"], scene
        assert scene["today"][1]["when"] == "earlier today"
        assert scene["nextNotice"]["title"] == "Festival of Blossoms" and scene["previousNotice"]["title"] == "Yuletide"
        assert scene["landmarks"][0]["direction"] == "east" and scene["interruptedActivity"] == "tending a field"
        assert scene["nearbyPeople"] == ["Anna"] and scene["bystandersPresent"] and scene["place"]["interior"] is False
        assert all(key not in scene for key in ("world", "nativeId", "owner", "agentInventory"))

    beat()
    observation("gift", "The player's private gift was a turquoise locket.")
    observation("worldEvent", "I saw the player at the Grand Joust.")
    visitor = next(row["key"] for row in api("/api/saves/"+campaign+"/characters") if row["owner"]=="Visitor-NPC")
    report = "I saw a merchant at the docks. Our private meeting place was the old kiln."
    source = uid()
    api("/api/module/cue", dict(id=source, parent=world["cueHead"], kind="group", people=[], world=world,
        line=dict(session=uid(),seq=1,speaker=twin,to=speaker,audience=[speaker,twin],
                  words=report,at=datetime.now(timezone.utc).isoformat())))
    world["cueHead"] = source
    beat()
    original = turn(deepcopy(facts))
    calls = len(model.calls)
    prepared = api(private, original)
    assert prepared["state"] == "ready" and len(model.calls) == calls + 1
    recall = private_recall()
    verify(recall["worldAwareness"])
    assert recall["eventWitnesses"][0]["description"] == "I saw the player at the Grand Joust."
    assert any(x.get("source")==source and x.get("quote")==report for x in recall["heardToday"])
    assert model.calls[-1]["messages"][1]["content"] == words
    assert api(private, dict(original, worldAwareness=dict(facts, weather="Snow"))) == prepared
    assert len(model.calls) == calls + 1
    checked("accepted private world, overlapping events on a sensitive turn, dated sighting, and one model call across replay")

    address = dict(id=uid(), target=dict(world=deepcopy(world), owner="East-NPC", nativeId="twin"),
                   head=world["cueHead"], card="Small Talk", cue="Greet the player.", worldAwareness=deepcopy(facts))
    result = api("/api/dialogue/address", address)
    verify(context()["worldAwareness"])
    assert "turquoise locket" not in str(context()) and "saw the player" in str(context())
    assert "old kiln" not in str(context())
    calls = len(model.calls)
    assert api("/api/dialogue/address", address) == result and len(model.calls) == calls

    group = dict(id=uid(), session=uid(), head=world["cueHead"], campaign=campaign, actor=speaker,
                 audience=[speaker,twin], worldAwareness=deepcopy(facts))
    result = api("/api/dialogue/group", group)
    verify(context()["worldAwareness"])
    assert "turquoise locket" not in str(context()) and "saw the player" in str(context()["yourMemory"])
    calls = len(model.calls)
    assert api("/api/dialogue/group", dict(group,worldAwareness=dict(facts, weather="Snow"))) == result
    api("/api/dialogue/group", dict(group,actor=twin),status=409)
    assert len(model.calls) == calls
    other = dict(facts,owner="West-NPC",place=dict(name="A quiet room",interior=True),today=[],nearbyPeople=[])
    for key in ("weather","landmarks","sights","interruptedActivity","bystandersPresent","nextNotice","previousNotice"):
        other.pop(key)
    api("/api/dialogue/group", dict(group,id=uid(),actor=twin,worldAwareness=other))
    assert context()["worldAwareness"]["place"]["interior"] and "saw the player" not in str(context())
    assert "weather" not in context()["worldAwareness"] and "Grand Joust" not in str(context())
    api("/api/dialogue/group", dict(group,id=uid(),audience=[speaker,visitor]))
    news = context()["yourMemory"]["heardToday"]
    assert any(x["subject"]=="merchant" and x["source"]==source and "quote" not in x for x in news)
    assert "old kiln" not in str(context())
    distant = dict(other,owner="Visitor-NPC",nativeId="linn")
    api("/api/dialogue/group", dict(group,id=uid(),actor=visitor,audience=[speaker,visitor],worldAwareness=distant))
    assert "heardToday" not in context()["yourMemory"] and "saw the player" not in str(context())
    checked("actual speaker per group turn, repeatable address/group requests, public recall excludes private gifts and another resident's witness")

    session = uid()
    offer = dict(key="native-flour", title="Flour", terms="Bring flour.", objectives=["Collect flour"])
    quest = dict(id=session, session=session, campaign=campaign, actor=speaker, key=offer["key"], offer=offer,
                 worldAwareness=deepcopy(facts))
    result = api("/api/quests/turn", quest)
    assert result["state"] == "ready" and result["action"] == "continue" and context()["nativeOffer"] == offer
    verify(context()["worldAwareness"])
    assert "surroundings" in model.calls[-1]["messages"][0]["content"]
    calls = len(model.calls)
    assert api("/api/quests/turn", dict(quest,worldAwareness=other)) == result and len(model.calls) == calls
    api("/api/quests/finish",dict(quest,action="continue"))
    event_reply = "I'll attend the Grand Joust."
    model.samples["quest/"+event_reply] = json.dumps(dict(intent="accept",evidence=event_reply,reply="Excellent."))
    question = dict(quest,id=uid(),parent=session,text=event_reply)
    question.pop("offer")
    incidental = api("/api/quests/turn",question)
    assert incidental["state"]=="ready" and incidental["action"]=="continue" and incidental["intent"]=="unclear"
    assert context()["nativeOffer"]==offer and context()["latestPlayerSpeech"]==event_reply
    api("/api/quests/finish",dict(question,action="continue"))
    assent = "I'll bring the flour."
    model.samples["quest/"+assent] = json.dumps(dict(intent="accept",evidence=assent,reply="Thank you."))
    assert api("/api/quests/turn",dict(question,id=uid(),parent=question["id"],text=assent))["action"]=="accept"
    checked("quest offer and action remain native with a separately bound world context and replay")

    # Wrong save, owner, day, history head, or retired load cannot contribute facts.
    for bad in [dict(facts,owner="West-NPC"),dict(facts,day=13),
                dict(facts,world=dict(world,slot=2)),dict(facts,world=dict(world,cueHead=uid()))]:
        assert api(private,turn(bad))["state"] == "ready" and "worldAwareness" not in private_recall()
    assert api(private,turn(None))["state"] == "ready" and "worldAwareness" not in private_recall()
    # A fresh accepted morning can precede the heartbeat at midnight.
    moved = dict(facts,day=13,world=dict(world,day=13),nextNotice=later)
    api(private,turn(moved))
    assert private_recall()["worldAwareness"]["day"] == 13
    assert private_recall()["worldAwareness"]["nextNotice"]["season"] == "Summer"
    bad_next = dict(facts,nextNotice=dict(joust,dayOfSeason=12))
    api(private,turn(bad_next))
    assert "nextNotice" not in private_recall()["worldAwareness"]
    old = deepcopy(facts)
    world["day"]=13
    beat()
    assert api(private,turn(old))["state"]=="ready" and "worldAwareness" not in private_recall()
    delayed=turn(deepcopy(old))
    delayed["worldAwareness"]["acceptedTurn"]=delayed["id"]
    assert api(private,delayed)["state"]=="ready" and private_recall()["worldAwareness"]["day"]==12
    world.update(memorySession=uid(),cueHead=None,day=12)
    beat()
    assert api(private,turn(old))["state"] == "ready" and "worldAwareness" not in private_recall()
    api(private,turn(dict(facts,world=world)))
    assert "eventWitnesses" not in private_recall()
    checked("older module compatibility, identity and day binding, future notice rollover, retired load and two save branches on the same date")
