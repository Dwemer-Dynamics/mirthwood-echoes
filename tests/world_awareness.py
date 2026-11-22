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
    ("I'll attend the Grand Joust.", "I'll attend the Grand Joust.", None, "accept", "continue"),
    ("I'll attend the Grand Joust.", "I'll attend", None, "accept", "continue"),
    ("I'll attend the Grand Joust.", "I'll attend", "world", "accept", "continue"),
    ("I'll bring the flour.", "I'll bring the flour.", None, "accept", "accept"),
    ("After the joust, I'll bring you the flour.", "I'll bring you the flour", "job", "accept", "accept"),
    ("I'm going to the joust, so I won't take the flour job.", "I won't take the flour job", "job", "decline", "decline"),
    ("I won't attend the Grand Joust.", "I won't", None, "decline", "continue"),
    ("Yes, I accept.", "Yes, I accept.", "job", "accept", "accept"),
    ("Yes, I accept. By the way, when is the Grand Joust?", "Yes, I accept.", "job", "accept", "accept"),
    ("I'll attend the Grand Joust. How much flour do you need?", "I'll attend", None, "accept", "continue"),
    ("I'll attend the Grand Joust. How much flour do you need?", "I'll attend the Grand Joust", None, "accept", "continue"),
    ("Yes, I'll bring the flour. By the way, when is the Grand Joust?", "Yes, I'll bring the flour.", "job", "accept", "accept"),
    ("I'll bring the flour. Do you know if the Grand Joust starts today?", "I'll bring the flour.", "job", "accept", "accept"),
    ("I'll bring the flour if the Grand Joust is cancelled.", "I'll bring the flour", "job", "accept", "continue"),
    ("Grand Joust! Sure, count me in.", "Sure, count me in.", None, "accept", "continue"),
    ("How much flour do you need? I'll attend the Grand Joust.", "I'll attend", None, "accept", "continue"),
    ("Sure, I'll attend the Grand Joust.", "Sure, I'll attend the Grand Joust.", "job", "accept", "continue"),
    ("No thanks, I won't attend the Grand Joust.", "No thanks, I won't attend the Grand Joust.", "job", "decline", "continue"),
    ("Sure.", "Sure.", "job", "accept", "accept"),
    ("Sure, I'll bring the flour.", "Sure, I'll bring the flour.", "job", "accept", "accept"),
    ("Yes, I'll attend the Grand Joust.", "Yes", "job", "accept", "continue"),
    ("Is the Grand Joust today? I accept.", "I accept.", "job", "accept", "accept"),
    ("Will I be at the joust? Yes.", "Yes", "job", "accept", "continue"),
    ("I'll bring the flour, if you pay double?", "I'll bring the flour", "job", "accept", "continue"),
    ("I won't bring the flour.", "I won't bring the flour.", "job", "accept", "continue"),
    ("I won't bring the flour.", "I won't bring the flour.", "job", "decline", "decline"),
    # Additional clause ordering, quotations, counteroffers and inverted polarity.
    ("When is the Grand Joust? Yes, I accept.", "Yes, I accept.", "job", "accept", "accept"),
    ("No, I decline. When is the joust?", "No, I decline.", "job", "decline", "decline"),
    ("When is the joust? No, I decline.", "I decline", "job", "decline", "decline"),
    ("How much flour do you need? I'll attend the Grand Joust.", "I'll attend", "job", "accept", "continue"),
    ("How much flour do you need? I'll attend the Grand Joust.", "I'll attend the Grand Joust.", "job", "accept", "continue"),
    ("I won't attend. How much flour do you need?", "I won't attend", "unsure", "decline", "continue"),
    ("After the joust, I'll bring you the flour.", "I'll bring you the flour", None, "accept", "continue"),
    ("Yes, I'll attend the joust.", "Yes", "job", "accept", "continue"),
    ("I accept the invitation to joust. I accept.", "I accept", "job", "accept", "continue"),
    ("I'll bring the flour. Do you know if the Grand Joust starts today?", "I'll bring the flour", "job", "accept", "accept"),
    ("I'll bring the flour if the joust is cancelled.", "I'll bring the flour", "job", "accept", "continue"),
    ("I'll bring the flour.", "I'll bring the flour.", "job", "decline", "continue"),
    ("I'll bring the flour.", "I'll bring the flour.", "job", "accept", "accept"),
    ("Yes, I'll do the work on the original terms.", "Yes, I'll do the work on the original terms.", "job", "accept", "accept"),
    ("I'll bring the flour if you double the pay.", "I'll bring the flour if you double the pay", "job", "counter", "continue"),
    ("Did you say “I accept the flour job”? I will attend the joust.", "I accept the flour job", "job", "accept", "continue"),
    ("I will attend the joust. Did he ask “Will you bring the flour?”", "Will you bring the flour", "job", "accept", "continue"),
    ("She asked “When is the joust?” I will bring the flour.", "I will bring the flour", "job", "accept", "accept"),
    ("Yes.", "Yes", None, "accept", "accept"),
    ("I'll bring flour recipes.", "I'll bring flour recipes.", "job", "accept", "continue"),
    ("I'm going to the joust. I'll bring the flour sack.", "I'm going to the joust. I'll bring the flour sack.", "job", "accept", "continue"),
    ("I won't take the flour mill job.", "I won't take the flour mill job.", "job", "decline", "continue"),
    ("I'll bring you some flour.", "I'll bring you some flour.", "job", "accept", "accept"),
    ("I won't take the flour job.", "I won't take the flour job.", "job", "decline", "decline"),
    ("Yes, on those terms.", "Yes, on those terms.", "job", "accept", "accept"),
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

    profile = api("/api/providers/openai_compatible")
    dialogue_model = profile["conversation"] or profile["defaultModel"]
    memory_model = profile["memory"]
    assert dialogue_model and memory_model and dialogue_model != memory_model
    expected_choices = {branch: answer[branch] for branch in ("warm", "neutral", "cool")}
    fixture_start = len(model.calls)
    expected_calls = 0

    def foreground_since(start):
        # Daily recaps run independently, including between an HTTP reply and this assertion.
        # An extra Talk packet counts even if it was routed to the wrong model.
        return [p for p in model.calls[start:] if p["model"] != memory_model or
                not p["messages"][0]["content"].startswith("Summarize one day of spoken dialogue")]

    def one_call_since(start, path, request_id):
        nonlocal expected_calls
        packets = foreground_since(start)
        assert len(packets) == 1, (path, request_id, start, [p["model"] for p in packets])
        packet = packets[0]
        assert packet["model"] == dialogue_model, (path, request_id, packet["model"])
        expected_calls += 1
        return packet

    def generate(path, body):
        start = len(model.calls)
        reply = api(path, body)
        return reply, one_call_since(start, path, body["id"])

    def without_generation(path, body, status=200):
        start = len(model.calls)
        reply = api(path, body, status=status)
        assert not foreground_since(start), (path, body["id"])
        return reply

    def prepare(body):
        reply, packet = generate(private, body)
        assert reply["state"] == "ready" and reply["choices"] == expected_choices, (body["id"], reply)
        system, player = packet["messages"]
        assert system["role"] == "system" and player == {"role": "user", "content": body["text"]}, body["id"]
        _, marker, data = system["content"].partition("\nRecall: ")
        assert marker, body["id"]
        recall = json.loads(data)
        assert isinstance(recall, dict) and isinstance(recall["recentSpeech"], list), body["id"]
        return reply, recall

    def context(packet):
        return json.loads(packet["messages"][1]["content"])

    def verify(scene):
        assert scene["day"] == 12 and scene["calendar"]["timeOfDay"] == "Morning", scene
        assert [e["title"] for e in scene["today"]] == ["Grand Joust", "Traveling merchant", "Another posted gathering"], scene
        assert scene["today"][1]["when"] == "earlier today"
        assert scene["nextNotice"]["title"] == "Festival of Blossoms" and scene["previousNotice"]["title"] == "Yuletide"
        assert set(scene["nextByKind"])=={"festival","joust"} and scene["nextByKind"]["festival"]==scene["nextNotice"]
        follow = scene["nextByKind"]["joust"]
        assert (follow["title"], follow["season"], follow["dayOfSeason"]) == ("Grand Joust", "Summer", 10)
        assert scene["calendar"]["daysInSeason"] == 25
        dates = [scene["calendar"], *scene["today"], scene["nextNotice"], scene["previousNotice"], *scene["nextByKind"].values()]
        assert all("year" not in date for date in dates), dates
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
    prepared, recall = prepare(original)
    verify(recall["worldAwareness"])
    assert recall["eventWitnesses"][0]["description"] == "I saw the player at the Grand Joust."
    assert any(x.get("source")==source and x.get("quote")==report for x in recall["heardToday"])
    assert without_generation(private, dict(original, worldAwareness=dict(facts, weather="Snow"))) == prepared
    checked("accepted private world, overlapping events on a sensitive turn, dated sighting, and one model call across replay")
    news_question = "What did you hear about the merchant?"
    model.samples[news_question] = json.dumps(answer)
    _, recall = prepare(turn(deepcopy(facts), text=news_question))
    for field in ("heardToday","heardSpeech"):
        assert any(x.get("source")==source and x.get("quote")==report for x in recall[field]),field

    address = dict(id=uid(), target=dict(world=deepcopy(world), owner="East-NPC", nativeId="twin"),
                   head=world["cueHead"], card="Small Talk", cue="Greet the player.", worldAwareness=deepcopy(facts))
    result, opener = generate("/api/dialogue/address", address)
    prompt = context(opener)
    verify(prompt["worldAwareness"])
    assert "turquoise locket" not in str(prompt) and "saw the player" in str(prompt)
    assert "old kiln" not in str(prompt)
    assert without_generation("/api/dialogue/address", address) == result

    # Drop a real client connection after the local provider has accepted its request.
    endpoint = urlsplit(base)
    lost = dict(address,id=uid())
    wire = json.dumps(lost).encode()
    def open_address():
        connection = http.client.HTTPConnection(endpoint.hostname,endpoint.port,timeout=5)
        connection.request("POST","/api/dialogue/address",wire,{"Content-Type":"application/json"})
        return connection

    entered, release = threading.Event(), threading.Event()
    gate_key = opener["messages"][1]["content"]
    model.gates[gate_key] = (entered,release)
    lost_start = len(model.calls)
    first = open_address()
    try:
        assert entered.wait(5), "the greeting did not reach the provider"
        first.sock.setsockopt(socket.SOL_SOCKET,socket.SO_LINGER,struct.pack("ii",1,0))
        first.close()
        without_generation("/api/dialogue/address",lost,status=425)
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
            lost_call = one_call_since(lost_start, "/api/dialogue/address", lost["id"])
            assert body==result and lost_call["messages"] == opener["messages"]
            break
        assert response.status==425 and time.monotonic()<until, (response.status,body)
        time.sleep(.05)
    checked("disconnect after the greeting provider starts returns the original reply once across repeated requests")

    group = dict(id=uid(), session=uid(), head=world["cueHead"], campaign=campaign, actor=speaker,
                 audience=[speaker,twin], worldAwareness=deepcopy(facts))
    result, packet = generate("/api/dialogue/group", group)
    prompt = context(packet)
    verify(prompt["worldAwareness"])
    assert "turquoise locket" not in str(prompt) and "saw the player" in str(prompt["yourMemory"])
    assert without_generation("/api/dialogue/group", dict(group,worldAwareness=dict(facts, weather="Snow"))) == result
    without_generation("/api/dialogue/group", dict(group,actor=twin),status=409)
    other = dict(facts,owner="West-NPC",place=dict(name="A quiet room",interior=True),today=[],nearbyPeople=[])
    for key in ("weather","landmarks","sights","interruptedActivity","bystandersPresent","nextNotice","previousNotice","nextByKind"):
        other.pop(key)
    _, packet = generate("/api/dialogue/group", dict(group,id=uid(),actor=twin,worldAwareness=other))
    prompt = context(packet)
    assert prompt["worldAwareness"]["place"]["interior"] and "saw the player" not in str(prompt)
    assert "weather" not in prompt["worldAwareness"] and "Grand Joust" not in str(prompt)
    _, packet = generate("/api/dialogue/group", dict(group,id=uid(),audience=[speaker,visitor]))
    prompt = context(packet)
    news = prompt["yourMemory"]["heardToday"]
    assert any(x["subject"]=="merchant" and x["source"]==source and "quote" not in x for x in news)
    assert "old kiln" not in str(prompt) and "The merchant never came" not in str(prompt)
    distant = dict(other,owner="Visitor-NPC",nativeId="linn")
    _, packet = generate("/api/dialogue/group", dict(group,id=uid(),actor=visitor,audience=[speaker,visitor],worldAwareness=distant))
    prompt = context(packet)
    assert "heardToday" not in prompt["yourMemory"] and "saw the player" not in str(prompt)
    checked("actual speaker per group turn, repeatable address/group requests, public recall excludes private gifts and another resident's witness")

    session = uid()
    offer = dict(key="native-flour", title="Flour", terms="Bring flour.", objectives=["Collect flour"])
    quest = dict(id=session, session=session, campaign=campaign, actor=speaker, key=offer["key"], offer=offer,
                 worldAwareness=deepcopy(facts))
    quest_route = "/api/quests/turn"

    def quest_turn(body):
        reply, _ = generate(quest_route, body)
        return reply

    result, packet = generate(quest_route, quest)
    prompt = context(packet)
    assert result["state"] == "ready" and result["action"] == "continue" and prompt["nativeOffer"] == offer
    verify(prompt["worldAwareness"])
    assert "surroundings" in packet["messages"][0]["content"]
    assert without_generation(quest_route, dict(quest,worldAwareness=other)) == result
    api("/api/quests/finish",dict(quest,action="continue"))
    event_reply = "I'll attend the Grand Joust."
    model.samples["quest/"+event_reply] = json.dumps(dict(intent="accept",evidence="I'll attend",reply="Excellent."))
    question = dict(quest,id=uid(),parent=session,text=event_reply)
    question.pop("offer")
    incidental, packet = generate(quest_route, question)
    assert incidental["state"]=="ready" and incidental["action"]=="continue" and incidental["intent"]=="unclear"
    prompt = context(packet)
    assert prompt["nativeOffer"]==offer and prompt["latestPlayerSpeech"]==event_reply
    api("/api/quests/finish",dict(question,action="continue"))
    assent = "After the joust, I'll bring you the flour."
    model.samples["quest/"+assent] = json.dumps(dict(intent="accept",subject="job",evidence="I'll bring you the flour",reply="Thank you."))
    assert quest_turn(dict(question,id=uid(),parent=question["id"],text=assent))["action"]=="accept"
    other_session = uid()
    second_offer = dict(quest,id=other_session,session=other_session)
    assert quest_turn(second_offer)["action"]=="continue"
    api("/api/quests/finish",dict(second_offer,action="continue"))
    refuse_event = "I won't attend the Grand Joust."
    model.samples["quest/"+refuse_event] = json.dumps(dict(intent="decline",evidence="I won't",reply="Take care."))
    event_choice = dict(question,id=uid(),session=other_session,parent=other_session,text=refuse_event)
    assert quest_turn(event_choice)["action"]=="continue"
    api("/api/quests/finish",dict(event_choice,action="continue"))
    refuse_job = "I'm going to the joust, so I won't take the flour job."
    model.samples["quest/"+refuse_job] = json.dumps(dict(intent="decline",subject="job",evidence="I won't take the flour job",reply="Take care."))
    assert quest_turn(dict(event_choice,id=uid(),parent=event_choice["id"],text=refuse_job))["action"]=="decline"
    checked("quest offer and action remain native with a separately bound world context and replay")

    def fresh_offer(native_offer=offer):
        root_id=uid()
        root=dict(quest,id=root_id,session=root_id,key=native_offer["key"],offer=native_offer)
        opening, packet = generate(quest_route, root)
        assert opening["state"]=="ready" and opening["action"]=="continue"
        assert context(packet)["nativeOffer"] == native_offer
        assert without_generation("/api/quests/finish",dict(root,action="continue"))["state"]=="complete"
        return dict(question,id=uid(),session=root_id,parent=root_id,key=native_offer["key"])

    def classified_turn(body, classification, native_offer=offer, previously_warned=False):
        model.samples["quest/"+body["text"]] = json.dumps(classification)
        actual, packet = generate(quest_route, body)
        prompt = context(packet)
        assert prompt["nativeOffer"] == native_offer and prompt["latestPlayerSpeech"] == body["text"], body["id"]
        assert prompt["alreadyRefusedToChangeTerms"] is previously_warned, body["id"]
        assert actual["state"] == "ready" and without_generation(quest_route, body) == actual, body["id"]
        return actual

    for speech,evidence,subject,intent,expected in QUEST_CHOICES:
        body=dict(fresh_offer(),text=speech)
        classification=dict(intent=intent,evidence=evidence,reply="I hear you.")
        if subject is not None:
            classification["subject"]=subject
        actual=classified_turn(body,classification)
        assert actual["action"]==expected, (speech,intent,actual)
        if intent=="counter":
            assert actual["warned"] and actual["intent"]=="counter"

    numbered = dict(key="native-red-apples", title="Red Apples", terms="Bring 3 red apples.", objectives=["Collect 3 red apples"])
    for speech, expected in [
        ("I'll bring you 3 green apples.", "continue"),
        ("I'll bring you 2 red apples.", "continue"),
        ("I'll bring you 3 red apples.", "accept"),
    ]:
        body = dict(fresh_offer(numbered), text=speech)
        classification = dict(intent="accept", subject="job", evidence=speech, offerDetail="apples", reply="I hear you.")
        actual = classified_turn(body, classification, numbered)
        assert actual["action"] == expected and actual["warned"] is False, (speech, actual)
    checked("a named job and a numbered offer preserve the requested item and quantity across replay")

    for speech, evidence, real_counter in [
        ("If it rains, I'll attend the Grand Joust instead.", "If it rains", False),
        ("No, unless you pay more", "No, unless you pay more", True),
        ("Can you pay double?", "Can you pay double?", True),
    ]:
        body = dict(fresh_offer(), text=speech)
        classification = dict(intent="counter", subject="job", termChange="reward", evidence=evidence, manner="plain")
        for repeated in (False, True):
            actual = classified_turn(body, classification, previously_warned=repeated and real_counter)
            action = "decline" if repeated and real_counter else "continue"
            assert actual["intent"] == ("counter" if real_counter else "unclear"), (speech, actual)
            assert actual["warned"] is real_counter and actual["action"] == action, (speech, repeated, actual)
            ack = dict(body, action=action)
            confirmed = without_generation("/api/quests/finish", ack)
            assert confirmed == dict(actual, state="complete")
            assert without_generation("/api/quests/finish", ack) == confirmed
            body = dict(body, id=uid(), parent=body["id"])
        if real_counter:
            without_generation(quest_route, body, status=409)
    checked("only a genuine reward counter warns on its first turn and closes the offer on its second")

    # A bare answer to the resident's subsequent question about attendance belongs to that question.
    body=dict(fresh_offer(),text="Are you going to the joust?")
    previous="I might. Will you be going?"
    model.samples["quest/"+body["text"]]=json.dumps(dict(intent="question",subject="world",evidence="",reply=previous))
    assert quest_turn(body)["reply"]==previous
    api("/api/quests/finish",dict(body,action="continue"))
    model.samples["quest/Yes."]=json.dumps(dict(intent="accept",subject="job",evidence="Yes",reply="Thank you."))
    actual, packet = generate(quest_route,dict(body,id=uid(),parent=body["id"],text="Yes."))
    assert not context(packet)["answeringOffer"] and actual["action"]=="continue"
    checked("complete quest consent clauses in both orders, native invitation, reported questions, conditions and one call across replay")

    # Wrong save, owner, day, history head, or retired load cannot contribute facts.
    for bad in [dict(facts,owner="West-NPC"),dict(facts,day=13),
                dict(facts,world=dict(world,slot=2)),dict(facts,world=dict(world,cueHead=uid()))]:
        _, recall = prepare(turn(bad))
        assert "worldAwareness" not in recall
    _, recall = prepare(turn(None))
    assert "worldAwareness" not in recall
    # A fresh accepted morning can precede the heartbeat at midnight.
    moved = dict(facts,day=13,world=dict(world,day=13),nextNotice=later)
    _, recall = prepare(turn(moved))
    assert recall["worldAwareness"]["day"] == 13
    assert recall["worldAwareness"]["nextNotice"]["season"] == "Summer"
    bad_next = dict(facts,nextNotice=dict(joust,dayOfSeason=12))
    _, recall = prepare(turn(bad_next))
    assert "nextNotice" not in recall["worldAwareness"]
    old = deepcopy(facts)
    world["day"]=13
    beat()
    _, recall = prepare(turn(old))
    assert "worldAwareness" not in recall
    delayed_words = "Which notices were on the board when I asked this morning?"
    model.samples[delayed_words] = json.dumps(answer)
    delayed=turn(deepcopy(old),text=delayed_words)
    delayed["worldAwareness"]["acceptedTurn"]=delayed["id"]
    saved_request = deepcopy(delayed)
    ready, recall = prepare(delayed)
    assert recall["worldAwareness"]["day"] == 12
    assert without_generation(private, saved_request) == ready
    world.update(memorySession=uid(),cueHead=None,day=12)
    beat()
    without_generation("/api/dialogue/address",address,status=409)
    without_generation("/api/dialogue/group",group,status=409)
    _, recall = prepare(turn(old))
    assert "worldAwareness" not in recall
    _, recall = prepare(turn(dict(facts,world=world)))
    assert "eventWitnesses" not in recall
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
    _, recall = prepare(turn(both_boards,text=festival_question))
    actual = recall["worldAwareness"]
    assert actual["nextNotice"]==actual["nextByKind"]["joust"] and actual["nextNotice"]["dayOfSeason"]==12
    festival = actual["nextByKind"]["festival"]
    assert (festival["title"],festival["dayOfSeason"],festival["source"])==("All Hallows Eve",17,"public notice")
    _, recall = prepare(turn(dict(both_boards,nextNotice=hallows,nextByKind=dict(festival=hallows,joust=dict(fall_joust,source="story list")))))
    assert set(recall["worldAwareness"]["nextByKind"])=={"festival"}
    checked("Fall 9 follow-up finds both the Fall 12 joust and Fall 17 festival; a visitor gets only a known public board")

    winter = dict(day, season="Winter", seasonIndex=3, dayOfSeason=25, year=1, timeOfDay="Night")
    spring = dict(day, season="Spring", seasonIndex=0, dayOfSeason=1, year=2, timeOfDay="Morning")
    past_yuletide = dict(previous, year=1)
    spring_joust = dict(joust, year=2)
    spring_festival = dict(next_notice, year=2)

    def visible_date(native):
        return {key: value for key, value in native.items() if key not in ("year", "seasonIndex")}

    for game_day, date, question in [
        (199, winter, "What is on the boards on this last night of winter, and what gathering comes next?"),
        (200, spring, "Now that spring has started, what was the last posted festival and when is the next joust?"),
    ]:
        world["day"] = game_day
        beat()
        at = {key: date[key] for key in ("season", "seasonIndex", "dayOfSeason", "year")}
        posted = dict(joust, **at, title="Brightoak public meeting")
        sighting = dict(market, **at)
        wrong_year = spring["year"] if date == winter else winter["year"]
        boundary = dict(facts, world=deepcopy(world), day=game_day, calendar=date,
                        today=[posted, dict(posted, title="Meeting in another year", year=wrong_year),
                               sighting, dict(sighting, title="Merchant sighting in another year", year=wrong_year)],
                        nextNotice=spring_joust, previousNotice=past_yuletide,
                        nextByKind=dict(joust=spring_joust, festival=spring_festival))
        model.samples[question] = json.dumps(answer)
        request = turn(boundary, text=question)
        ready, recall = prepare(request)
        actual = recall["worldAwareness"]
        assert actual["day"] == game_day and actual["calendar"] == visible_date(date)
        assert actual["today"] == [visible_date(posted), visible_date(sighting)], actual["today"]
        assert actual["nextNotice"] == visible_date(spring_joust) and actual["previousNotice"] == visible_date(past_yuletide)
        assert actual["nextByKind"] == dict(joust=actual["nextNotice"], festival=visible_date(spring_festival))
        if date == winter:
            winter_request, winter_ready = deepcopy(request), ready
    assert without_generation(private, winter_request) == winter_ready

    question = "Do the old and upcoming notices belong in that order?"
    model.samples[question] = json.dumps(answer)
    reversed_notices = dict(boundary, nextNotice=past_yuletide, previousNotice=spring_festival,
                            nextByKind=dict(joust=past_yuletide, festival=spring_festival))
    _, recall = prepare(turn(reversed_notices, text=question))
    actual = recall["worldAwareness"]
    assert "nextNotice" not in actual and "previousNotice" not in actual
    assert actual["nextByKind"] == {"festival": visible_date(spring_festival)}
    checked("Winter 25 to Spring 1 uses native years to select today's and adjacent notices, then shows only season and day")
    assert len(foreground_since(fixture_start)) == expected_calls
