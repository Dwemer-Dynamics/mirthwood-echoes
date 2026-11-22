"""Exercise the installed server's real prompt builders with two same-ID residents and a local model."""
import json
import http.client
import socket
import struct
import threading
import time
from copy import deepcopy
from datetime import datetime, timezone
from uuid import uuid4
from urllib.parse import urlsplit


# Classifier fixtures deliberately include truncated quotes and incorrect subject assignments.
QUEST_CHOICES = [
    ("Yes, I accept. By the way, when is the Grand Joust?", "Yes, I accept.", "job", "accept", "accept"),
    ("When is the Grand Joust? Yes, I accept.", "Yes, I accept.", "job", "accept", "accept"),
    ("No, I decline. When is the joust?", "No, I decline.", "job", "decline", "decline"),
    ("When is the joust? No, I decline.", "I decline", "job", "decline", "decline"),
    ("I'll attend the Grand Joust. How much flour do you need?", "I'll attend", None, "accept", "continue"),
    ("How much flour do you need? I'll attend the Grand Joust.", "I'll attend", "job", "accept", "continue"),
    ("How much flour do you need? I'll attend the Grand Joust.", "I'll attend the Grand Joust.", "job", "accept", "continue"),
    ("I won't attend. How much flour do you need?", "I won't attend", "unsure", "decline", "continue"),
    ("After the joust, I'll bring you the flour.", "I'll bring you the flour", None, "accept", "continue"),
    ("Yes, I'll attend the joust.", "Yes", "job", "accept", "continue"),
    ("Will I be at the joust? Yes.", "Yes", "job", "accept", "continue"),
    ("I accept the invitation to joust. I accept.", "I accept", "job", "accept", "continue"),
    ("I'll bring the flour. Do you know if the Grand Joust starts today?", "I'll bring the flour", "job", "accept", "accept"),
    ("I'll bring the flour if the joust is cancelled.", "I'll bring the flour", "job", "accept", "continue"),
    ("I'll bring the flour if you double the pay.", "I'll bring the flour if you double the pay", "job", "counter", "continue"),
    ('Did you say “I accept the flour job”? I will attend the joust.', "I accept the flour job", "job", "accept", "continue"),
    ('I will attend the joust. Did he ask “Will you bring the flour?”', "Will you bring the flour", "job", "accept", "continue"),
    ('She asked “When is the joust?” I will bring the flour.', "I will bring the flour", "job", "accept", "accept"),
    ("Yes.", "Yes", None, "accept", "accept"),
]


def check_world_awareness(api, model, campaign, speaker, twin, checked, base):
    def uid():
        return str(uuid4())

    load = uid()
    world = dict(slot=1, created="2025-01-03T12:00:00", player="p", day=12, memorySession=load, cueHead=None)
    day = dict(season="Spring", seasonIndex=0, dayOfSeason=14, daysInSeason=25, year=1, timeOfDay="Morning")
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
                 nextNotice=next_notice, previousNotice=previous,
                 nextByKind=dict(festival=next_notice,joust=later,random=dict(later,title="Unannounced visitor")),
                 agentInventory="sealed private letter")
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
        assert set(scene["nextByKind"])=={"festival","joust"} and scene["nextByKind"]["festival"]==scene["nextNotice"]
        follow = scene["nextByKind"]["joust"]
        assert (follow["title"], follow["season"], follow["dayOfSeason"]) == ("Grand Joust", "Summer", 10)
        assert scene["calendar"]["daysInSeason"] == 25 and "year" not in scene["previousNotice"]
        assert scene["landmarks"][0]["direction"] == "east" and scene["interruptedActivity"] == "tending a field"
        assert scene["nearbyPeople"] == ["Anna"] and scene["bystandersPresent"] and scene["place"]["interior"] is False
        assert all(key not in scene for key in ("world", "nativeId", "owner", "agentInventory"))

    beat()
    observation("gift", "The player's private gift was a turquoise locket.")
    observation("worldEvent", "I saw the player at the Grand Joust.")
    visitor = next(row["key"] for row in api("/api/saves/"+campaign+"/characters") if row["owner"]=="Visitor-NPC")
    report = ("I heard the merchant had arrived at the docks. Our private meeting place was the old kiln. "
              "The carter saw a blue wagon take the road to the landing, and two of the apprentices ran to find the dockmaster. "
              "The ferryman could see crates piled up by the rails. Everyone at the inn had heard about that wagon by the time I got there. "
              "That's the story I was asked to pass on. I've since spoken to the dockmaster myself. The merchant never came.")
    assert 360<report.index("The merchant never came") and len(report)<=1200
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
    news_question = "What did you hear about the merchant?"
    model.samples[news_question] = json.dumps(answer)
    api(private,turn(deepcopy(facts),text=news_question))
    recall = private_recall()
    for field in ("heardToday","heardSpeech"):
        assert any(x.get("source")==source and x.get("quote")==report for x in recall[field]),field

    address = dict(id=uid(), target=dict(world=deepcopy(world), owner="East-NPC", nativeId="twin"),
                   head=world["cueHead"], card="Small Talk", cue="Greet the player.", worldAwareness=deepcopy(facts))
    result = api("/api/dialogue/address", address)
    verify(context()["worldAwareness"])
    assert "turquoise locket" not in str(context()) and "saw the player" in str(context())
    assert "old kiln" not in str(context())
    calls = len(model.calls)
    assert api("/api/dialogue/address", address) == result and len(model.calls) == calls

    # Drop a real client connection after the local provider has accepted its request.
    endpoint = urlsplit(base)
    lost = dict(address,id=uid())
    wire = json.dumps(lost).encode()
    def open_address():
        connection = http.client.HTTPConnection(endpoint.hostname,endpoint.port,timeout=5)
        connection.request("POST","/api/dialogue/address",wire,{"Content-Type":"application/json"})
        return connection

    entered, release = threading.Event(), threading.Event()
    gate_key = model.calls[-1]["messages"][1]["content"]
    model.gates[gate_key] = (entered,release)
    first = open_address()
    try:
        assert entered.wait(5), "the greeting did not reach the provider"
        first.sock.setsockopt(socket.SOL_SOCKET,socket.SO_LINGER,struct.pack("ii",1,0))
        first.close()
        api("/api/dialogue/address",lost,status=425)
    finally:
        first.close()
        release.set()
        model.gates.pop(gate_key,None)
    until = time.monotonic()+5
    while True:
        retry = open_address()
        try:
            response = retry.getresponse()
            body = json.loads(response.read())
        finally:
            retry.close()
        if response.status==200:
            assert body==result and len(model.calls)==calls+1
            break
        assert response.status==425 and time.monotonic()<until, (response.status,body)
        time.sleep(.05)
    checked("disconnect after the greeting provider starts returns the original reply once across repeated requests")

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
    for key in ("weather","landmarks","sights","interruptedActivity","bystandersPresent","nextNotice","previousNotice","nextByKind"):
        other.pop(key)
    api("/api/dialogue/group", dict(group,id=uid(),actor=twin,worldAwareness=other))
    assert context()["worldAwareness"]["place"]["interior"] and "saw the player" not in str(context())
    assert "weather" not in context()["worldAwareness"] and "Grand Joust" not in str(context())
    api("/api/dialogue/group", dict(group,id=uid(),audience=[speaker,visitor]))
    news = context()["yourMemory"]["heardToday"]
    assert any(x["subject"]=="merchant" and x["source"]==source and "quote" not in x for x in news)
    assert "old kiln" not in str(context()) and "The merchant never came" not in str(context())
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
    model.samples["quest/"+event_reply] = json.dumps(dict(intent="accept",evidence="I'll attend",reply="Excellent."))
    question = dict(quest,id=uid(),parent=session,text=event_reply)
    question.pop("offer")
    incidental = api("/api/quests/turn",question)
    assert incidental["state"]=="ready" and incidental["action"]=="continue" and incidental["intent"]=="unclear"
    assert context()["nativeOffer"]==offer and context()["latestPlayerSpeech"]==event_reply
    api("/api/quests/finish",dict(question,action="continue"))
    assent = "After the joust, I'll bring you the flour."
    model.samples["quest/"+assent] = json.dumps(dict(intent="accept",subject="job",evidence="I'll bring you the flour",reply="Thank you."))
    assert api("/api/quests/turn",dict(question,id=uid(),parent=question["id"],text=assent))["action"]=="accept"
    other_session = uid()
    second_offer = dict(quest,id=other_session,session=other_session)
    assert api("/api/quests/turn",second_offer)["action"]=="continue"
    api("/api/quests/finish",dict(second_offer,action="continue"))
    refuse_event = "I won't attend the Grand Joust."
    model.samples["quest/"+refuse_event] = json.dumps(dict(intent="decline",evidence="I won't",reply="Take care."))
    event_choice = dict(question,id=uid(),session=other_session,parent=other_session,text=refuse_event)
    assert api("/api/quests/turn",event_choice)["action"]=="continue"
    api("/api/quests/finish",dict(event_choice,action="continue"))
    refuse_job = "I'm going to the joust, so I won't take the flour job."
    model.samples["quest/"+refuse_job] = json.dumps(dict(intent="decline",subject="job",evidence="I won't take the flour job",reply="Take care."))
    assert api("/api/quests/turn",dict(event_choice,id=uid(),parent=event_choice["id"],text=refuse_job))["action"]=="decline"
    checked("quest offer and action remain native with a separately bound world context and replay")

    def fresh_offer():
        root_id=uid()
        root=dict(quest,id=root_id,session=root_id)
        assert api("/api/quests/turn",root)["action"]=="continue"
        api("/api/quests/finish",dict(root,action="continue"))
        return dict(question,id=uid(),session=root_id,parent=root_id)

    for speech,evidence,subject,intent,expected in QUEST_CHOICES:
        body=dict(fresh_offer(),text=speech)
        model.samples["quest/"+speech]=json.dumps(dict(intent=intent,evidence=evidence,subject=subject,reply="I hear you."))
        calls=len(model.calls)
        actual=api("/api/quests/turn",body)
        assert actual["state"]=="ready" and actual["action"]==expected and len(model.calls)==calls+1, (speech,actual)
        if intent=="counter":
            assert actual["warned"] and actual["intent"]=="counter"
        assert api("/api/quests/turn",body)==actual and len(model.calls)==calls+1

    # A bare answer to the resident's subsequent question about attendance belongs to that question.
    body=dict(fresh_offer(),text="Are you going to the joust?")
    previous="I might. Will you be going?"
    model.samples["quest/"+body["text"]]=json.dumps(dict(intent="question",subject="world",evidence="",reply=previous))
    assert api("/api/quests/turn",body)["reply"]==previous
    api("/api/quests/finish",dict(body,action="continue"))
    model.samples["quest/Yes."]=json.dumps(dict(intent="accept",subject="job",evidence="Yes",reply="Thank you."))
    actual=api("/api/quests/turn",dict(body,id=uid(),parent=body["id"],text="Yes."))
    assert not context()["answeringOffer"] and actual["action"]=="continue"
    checked("complete quest consent clauses in both orders, native invitation, reported questions, conditions and one call across replay")

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
    calls = len(model.calls)
    api("/api/dialogue/address",address,status=409)
    api("/api/dialogue/group",group,status=409)
    assert len(model.calls) == calls
    assert api(private,turn(old))["state"] == "ready" and "worldAwareness" not in private_recall()
    api(private,turn(dict(facts,world=world)))
    assert "eventWitnesses" not in private_recall()
    checked("older module compatibility, identity and day binding, future notice rollover, retired load and two save branches on the same date")

    world["day"]=58
    beat()
    fall = dict(season="Fall",seasonIndex=2,dayOfSeason=9,daysInSeason=25,year=0,timeOfDay="Night")
    fall_joust = dict(joust,season="Fall",seasonIndex=2,dayOfSeason=12,year=0)
    hallows = dict(fall_joust,title="All Hallows Eve",dayOfSeason=17)
    both_boards = dict(facts,world=world,day=58,calendar=fall,today=[],nextNotice=fall_joust,
                       nextByKind=dict(joust=fall_joust,festival=hallows))
    festival_question = "And what is on the next festival notice?"
    model.samples[festival_question] = json.dumps(answer)
    api(private,turn(both_boards,text=festival_question))
    actual = private_recall()["worldAwareness"]
    assert actual["nextNotice"]==actual["nextByKind"]["joust"] and actual["nextNotice"]["dayOfSeason"]==12
    festival = actual["nextByKind"]["festival"]
    assert (festival["title"],festival["dayOfSeason"],festival["source"])==("All Hallows Eve",17,"public notice")
    api(private,turn(dict(both_boards,nextNotice=hallows,nextByKind=dict(festival=hallows,joust=dict(fall_joust,source="story list")))))
    assert set(private_recall()["worldAwareness"]["nextByKind"])=={"festival"}
    checked("Fall 9 follow-up finds both the Fall 12 joust and Fall 17 festival; a visitor gets only a known public board")
