"""Verify learning, day boundaries and durable delivery receipts against the actual server and database."""
from datetime import datetime, timezone
import json
import time
from uuid import uuid4


def check(api, beat, world, campaign, residents, sql, calls):
    averil, drystan, guest = residents
    load = str(uuid4())
    world["memorySession"] = load
    beat(20)
    prefix = "saves/" + campaign + "/characters/"

    def view(actor, section):
        return api(prefix + actor + "/history/" + section)["items"]

    def say(words, actor=averil):
        packet = dict(campaign=campaign, actor=actor, id=str(uuid4()), text=words)
        assert api("dialogue", packet)[-1]["type"] == "done"
        return packet

    def until(predicate):
        end = time.monotonic() + 25
        while not predicate():
            if time.monotonic() >= end:
                raise AssertionError("The memory worker did not complete the expected day transition")
            time.sleep(.1)

    def values(actor):
        return {item["value"].lower() for item in view(actor, "knowledge")}

    old = say("My favorite food is plum tart. The copper thimble fell off the gate.")
    say("*I smile.* I like fishing.")
    say("My favorite color is green.")
    say("Green is no longer my favorite color.")
    say("Actually, my favorite color is blue.")
    say("I used to like chess.")
    say("My brother likes red. My coat is purple. I like green apples.")
    say("If I liked crab pie, I would order it.")
    say("My father passed away.")
    assert {"plum tart", "fishing", "green", "blue", "chess", "green apples"} <= values(averil)
    assert not {"crab pie", "red", "purple"} & values(averil)
    beliefs = view(averil, "knowledge")
    assert all(r["earlier"] for r in beliefs if r["value"]=="green")
    assert any(r["stance"]=="retracted" for r in beliefs if r["value"]=="Green")
    assert next(r for r in beliefs if r["value"] == "chess")["stance"] == "former"
    assert any(r["kind"] == "moment" for r in view(averil, "commitments"))
    assert "plum tart" not in values(drystan)
    session = str(uuid4())
    packets = []

    def group(who, words, audience, **extra):
        packet = dict(id=str(uuid4()), parent=world.get("cueHead"), world=dict(world), kind="group", people=[],
                      line=dict(session=session, seq=len(packets)+1, speaker=who, to="group", words=words,
                                at=datetime.now(timezone.utc).isoformat(), audience=audience, **extra))
        api("module/cue", packet)
        packets.append(packet)
        world["cueHead"] = packet["id"]
        beat(world["day"])
        return packet["id"]

    both = [averil, drystan]
    group(averil, "What is your favorite color?", both)
    answer = group("player", "Green.", [*both, guest])
    assert "green" in values(drystan) and "green" not in values(guest)
    evidence = next(r for r in view(drystan, "knowledge") if r["source"] == answer)
    assert evidence["question"] == "What is your favorite color?"
    group("player", "My favorite color is gold.", both)
    group(averil, "Test player likes dancing.", both)
    assert any(r["subject"]=="Test player" and r["speakerLabel"]!="Test player" and r["value"]=="dancing"
               for r in view(drystan,"knowledge"))
    group("player", "The magpie stole a buckle.", both)
    full_head = world["cueHead"]
    world["cueHead"] = answer
    beat(20)
    assert "gold" not in values(drystan)
    world["cueHead"] = full_head
    beat(20)
    assert "gold" in values(drystan)
    api("dialogue/group", dict(campaign=campaign, actor=averil, head=full_head, session=session,
                               audience=both, cue="Leisure"))
    context = json.loads(next(call["messages"][1]["content"] for call in reversed(calls)
                              if "heardInOrder" in call["messages"][1]["content"]))
    assert "fishing" not in str(context["yourMemory"].get("characterKnowledge",[]))

    for words,allowed in [("Don't tell everyone my favorite food.",False),("Please tell everyone my favorite food.",True)]:
        tip=group("player",words,both)
        api("dialogue/group",dict(campaign=campaign,actor=averil,head=tip,session=session,audience=both))
        prompt=json.loads(next(call["messages"][1]["content"] for call in reversed(calls) if "heardInOrder" in call["messages"][1]["content"]))
        assert ("plum tart" in str(prompt["yourMemory"].get("characterKnowledge",[])))==allowed
    full_head=world["cueHead"]
    # Unseen alternatives and model annotations alone never grant knowledge.
    alternate="Do you still keep a stone from the river?"
    conditional=dict(id=str(uuid4()),campaign=campaign,actor=averil,text=alternate)
    assert api("dialogue/prepare",conditional)["state"]=="ready"
    assert "ruby" not in values(averil)
    assert api("dialogue/finish",dict(conditional,reaction="neutral"))["reply"]=="I do."
    assert "ruby" not in values(averil)

    # A native return card may nominate an interest without the player naming it.
    native = dict(id=str(uuid4()), campaign=campaign, actor=averil, text="Do you still keep a quiet afternoon?",
                  returnCues={"Leisure":"Miller talks about leisure"})
    prepared=api("dialogue/prepare",native)
    assert prepared["state"]=="ready",prepared
    native_prompt=next(call["messages"][0]["content"] for call in reversed(calls) if call["messages"][1]["content"]==native["text"])
    private_memory=json.JSONDecoder().raw_decode(native_prompt.split("\nRecall: ",1)[1])[0]
    assert any(r["value"]=="fishing" for r in private_memory.get("characterKnowledge",[]))
    api("dialogue/finish",dict(native,reaction="neutral"))
    say("I promise to deliver flour tomorrow.")
    beat(21)
    assert any(r["id"]=="day-20" and r["visits"]>=2 for r in view(averil,"sessions"))
    assert any(r["source"]==old["id"] for r in api(prefix+averil+"/history/sessions/day-20"))
    beat(49)
    assert any(r["id"]==old["id"] for r in view(averil,"conversations"))
    clock = lambda: int(sql("SELECT elapsed FROM dialogue_clock WHERE campaign='"+campaign+"'"))
    elapsed = clock()
    beat(49)
    assert clock()==elapsed
    # Restoring an earlier save consumes no days. Reaching the next date in that load consumes one.
    world["memorySession"]=str(uuid4());beat(25)
    assert clock()==elapsed
    current_load=world["memorySession"]
    world["memorySession"]=load;beat(65)
    assert clock()==elapsed
    world["memorySession"]=current_load;beat(25)
    fresh=say("The alder tree has a crooked limb.")
    beat(26)
    until(lambda: sql("SELECT dialogue_expired FROM turns WHERE id='"+old["id"]+"'")=="t")
    assert any(r["id"]==fresh["id"] for r in view(averil,"conversations"))
    assert not any(r["id"]==old["id"] for r in view(averil,"conversations"))
    for table in ("turns","cue_events","memory_summaries","resident_recaps","dialogue_day_recaps","daily_events"):
        assert sql("SELECT count(*) FROM "+table+" t WHERE to_jsonb(t)::text LIKE ANY(ARRAY['%copper thimble%','%magpie stole%'])")=="0",table
    assert {"fishing","plum tart"}<=values(averil) and "gold" in values(drystan)
    assert any(r["kind"]=="promise" for r in view(averil,"commitments"))
    assert any(r["kind"]=="marked" for r in view(averil,"commitments"))
    assert any(r["kind"]=="moment" for r in view(averil,"commitments"))
    assert view(averil,"daily")
    assert api("dialogue",old)[-1]["expired"]
    api("dialogue",dict(old,text="Different words."),expected=409)
    assert api("dialogue/prepare",native)["state"]=="expired"
    assert api("dialogue/finish",dict(native,reaction="neutral"))["state"]=="expired"
    api("dialogue/prepare",dict(native,text="Different words."),expected=409)
    api("module/cue",packets[-1])
    api("module/cue",dict(packets[-1],line=dict(packets[-1]["line"],words="Changed.")),expected=409)
    assert sql("SELECT count(*) FROM cue_events t WHERE payload::text LIKE '%magpie stole%'")=="0"
    # An explicit question can recover a durable detail after every original line has expired.
    say("What is my favorite food?")
    prompt=next(call["messages"][0]["content"] for call in reversed(calls) if call["messages"][1]["content"]=="What is my favorite food?")
    knowledge=json.loads(prompt.split("\nRecall: ",1)[1])["characterKnowledge"]
    assert any(r["value"]=="plum tart" and r["saidBy"]=="Test player" for r in knowledge)
    print("PASS 30-day physical expiry, reload clock, preserved new branch speech, group witnesses, short answers, corrections, exact/conflicting replays, durable preferences, commitments and major moments.",flush=True)
