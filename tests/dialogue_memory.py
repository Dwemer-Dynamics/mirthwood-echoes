"""Verify learning, day boundaries and durable delivery receipts against the actual server and database."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
import time
from uuid import uuid4


def check(api, beat, world, campaign, residents, sql, calls, waiting, release):
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
    marked=say("The copper thimble fell off the gate. My father passed away.")
    api(prefix+averil+"/personality",dict(id=marked["id"],sourceType="turn",axis=1,direction=1,reason="Michael spoke about losing his father."),expected=204)
    turning=next(r for r in view(averil,"commitments") if r["kind"]=="marked" and r["source"]==marked["id"])
    assert turning["evidence"]=={"player":"My father passed away."},turning
    say("I love you. I like it here. I hate when it rains.")
    say("I like talking to you. I love being with him.")
    address=str(uuid4())
    api("module/cue",dict(world=dict(world),kind="address",parent=world["cueHead"],id=address,
                          people=[dict(owner="East-NPC",nativeId="twin",description="The copper thimble fell off the gate. Please forgive me.")]))
    world["cueHead"]=address;beat(20)
    observed=sql("SELECT id FROM daily_events WHERE cue_anchor='"+address+"'")
    api(prefix+averil+"/personality",dict(id=observed,sourceType="event",axis=1,direction=1,reason="She said Please forgive me."),expected=204)
    marked_address=next(r for r in view(averil,"commitments") if r["kind"]=="marked" and r["source"]==observed)
    assert marked_address["evidence"]=={"observation":"Please forgive me."},marked_address
    assert {"plum tart", "fishing", "green", "blue", "chess", "green apples"} <= values(averil)
    assert not {"crab pie", "red", "purple", "you", "it here", "when it rains", "talking to you", "being with him"} & values(averil)
    beliefs = view(averil, "knowledge")
    assert all(r["earlier"] for r in beliefs if r["value"]=="green")
    assert any(r["stance"]=="retracted" for r in beliefs if r["value"]=="Green")
    assert next(r for r in beliefs if r["value"] == "chess")["stance"] == "former"
    assert any(r["kind"] == "moment" for r in view(averil, "commitments"))
    assert "plum tart" not in values(drystan)
    play,visit=str(uuid4()),str(uuid4())
    def private(actor,words):
        packet=dict(id=str(uuid4()),campaign=campaign,actor=actor,text=words,
                    context=dict(session=play,conversation=visit,head=world["cueHead"]))
        assert api("dialogue/prepare",packet)["state"]=="ready"
        api("module/cue",dict(world=dict(world),kind="talk",people=[],parent=world["cueHead"],id=packet["id"]))
        assert api("dialogue/finish",dict(packet,reaction="neutral"))["state"]=="complete"
        world["cueHead"]=packet["id"];beat(world["day"])
        return packet["id"]
    private(drystan,"Could I ask about the allotment?")
    loaf=private(drystan,"Rye bread.")
    green=private(drystan,"Green.")
    details=view(drystan,"knowledge")
    assert next(r for r in details if r["source"]==loaf)["question"]=="What is your favorite food?"
    assert next(r for r in details if r["source"]==green)["question"]=="What is your favorite color?"
    private_promise=private(averil,"I promise to mend the green banner tomorrow.")
    pledge=next(r["id"] for r in view(averil,"commitments") if r.get("sourceTurn")==private_promise)
    session = str(uuid4())
    packets = []

    def group(who, words, audience, to="group", **extra):
        packet = dict(id=str(uuid4()), parent=world.get("cueHead"), world=dict(world), kind="group", people=[],
                      line=dict(session=session, seq=len(packets)+1, speaker=who, to=to, words=words,
                                at=datetime.now(timezone.utc).isoformat(), audience=audience, **extra))
        api("module/cue", packet)
        packets.append(packet)
        world["cueHead"] = packet["id"]
        beat(world["day"])
        return packet["id"]

    both = [averil, drystan]
    group(averil, "The copper thimble fell off the gate. What is your favorite color?", both)
    answer = group("player", "Green.", [*both, guest])
    assert "green" in values(drystan) and "green" not in values(guest)
    evidence = next(r for r in view(drystan, "knowledge") if r["source"] == answer)
    assert evidence["question"] == "What is your favorite color?"
    group("player", "My favorite color is gold.", both)
    group(averil, "Test player likes dancing.", both)
    assert any(r["subject"]=="Test player" and r["speakerLabel"]!="Test player" and r["value"]=="dancing"
               for r in view(drystan,"knowledge"))
    group("player", "The magpie stole a buckle.", both)
    group(averil,"What is your favorite color?",[*both,guest],to=drystan)
    wrong=group(guest,"Teal.",[*both,guest])
    group(averil,"What is your favorite food? I'm curious.",both)
    ambiguous=group("player","Honey cake.",both)
    assert all(r["source"] not in (wrong,ambiguous) for actor in residents for r in view(actor,"knowledge"))
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
    ribbon=say("Could you bring a ribbon to the fair?")
    agreement=dict(id=str(uuid4()),campaign=campaign,actor=averil,text="Did you hear my offer?")
    assert api("dialogue/prepare",agreement)["state"]=="ready"
    staged=json.loads(sql(f"""SELECT talk FROM turns WHERE id='{agreement["id"]}'"""))
    assert staged["commitments"][0]["proposal"]["turn"]==ribbon["id"] and "ribbon" not in json.dumps(staged)
    assert api("dialogue/finish",dict(agreement,reaction="neutral"))["state"]=="complete"
    assert sql(f"""SELECT proposal FROM commitments WHERE turn_id='{agreement["id"]}'""")==ribbon["text"]
    fish=say("We landed a speckled trout beneath the alder footbridge.")
    repair=say("We replaced the broken planks at the alder footbridge.")
    hive=say("The orchard bees swarmed beside the tumbledown chapel.")
    craft=say("The glassblower shaped a blue goblet at the bell tower.")
    assert any(r["id"]==fish["id"] for r in view(averil,"conversations"))
    expiring=say("Could you bring the copper thimble to the mill?")
    delayed=dict(id=str(uuid4()),campaign=campaign,actor=averil,text="Would you do that for me?")
    with ThreadPoolExecutor(max_workers=1) as pool:
        future=pool.submit(api,"dialogue/prepare",delayed)
        try:
            assert waiting.wait(5),"The provider did not receive the delayed reply"
            prompt=next(call["messages"][0]["content"] for call in reversed(calls) if call["messages"][1]["content"]==delayed["text"])
            assert expiring["text"] in prompt
            beat(21)
            until(lambda: sql("SELECT dialogue_expired FROM turns WHERE id='"+expiring["id"]+"'")=="t")
        finally:release.set()
        assert future.result()["state"]=="ready"
    assert api("dialogue/finish",dict(delayed,reaction="neutral"))["state"]=="complete"
    assert sql(f"""SELECT count(*) FROM commitments WHERE turn_id='{delayed["id"]}'""")=="0"
    assert "copper thimble" not in sql(f"""SELECT talk FROM turns WHERE id='{delayed["id"]}'""")
    print("PASS a proposal retired during generation cannot return through a pending reply.",flush=True)
    def check_recall(actor,query):
        say(query,actor)
        prompt=next(call["messages"][0]["content"] for call in reversed(calls) if call["messages"][1]["content"]==query)
        return json.loads(prompt.split("\nRecall: ",1)[1]).get("conversationRecaps",[])
    def texts(items):return " ".join(r["note"].lower() for r in items)
    def day_recap(actor):
        return next(r for r in view(actor,"sessions") if r["id"]=="day-20")
    assert day_recap(averil)["visits"]>=2 and day_recap(averil)["hasOriginal"] is False,day_recap(averil)
    api(prefix+averil+"/history/sessions/day-20",expected=404)
    api(prefix+averil+"/sources/turn/"+fish["id"],expected=404)
    ask="Which quiet stretch of river would you recommend for angling?"
    remembered=check_recall(averil,ask)
    assert "alder footbridge" in texts(remembered) and "broken planks" not in texts(remembered),remembered
    assert any(r["speaker"]=="Test player" and r["day"]==20 for r in remembered)
    assert "planks" in texts(check_recall(averil,"How are the repairs coming along?"))
    assert "chapel" in texts(check_recall(averil,"Is this a good place for an apiary?"))
    until(lambda: sql(f"""SELECT aliases FROM dialogue_recap_parts WHERE source='{craft["id"]}' AND pos=1""")=="glassmaking stemware")
    assert "bell tower" in texts(check_recall(averil,"Who around here knows about stemware?"))
    assert not check_recall(averil,"Have you seen any emeralds?")
    assert not check_recall(drystan,ask)
    assert sql(f"""SELECT player_text='' AND reply IS NULL FROM turns WHERE id='{fish["id"]}'""")=="t"
    assert not any(r["id"]==old["id"] for r in view(averil,"conversations"))
    # Verify the same visible source boundaries after the group's original words have gone.
    group_entry="day-20-"+session
    def group_body(actor):return next((r["body"] for r in view(actor,"sessions") if r["id"]==group_entry),"")
    assert group_body(drystan) and "magpie" not in group_body(guest).lower()
    world["cueHead"]=packets[0]["id"];beat(21)
    assert group_body(drystan) and not group_body(guest)
    world["cueHead"]=full_head;beat(21)
    beat(49)
    assert day_recap(averil) and "alder" in texts(check_recall(averil,ask))
    clock=lambda:int(sql("SELECT elapsed FROM dialogue_clock WHERE campaign='"+campaign+"'"))
    elapsed=clock();beat(49);assert clock()==elapsed
    world["memorySession"]=str(uuid4());beat(20);assert clock()==elapsed
    current_load=world["memorySession"]
    world["memorySession"]=load;beat(65);assert clock()==elapsed
    world["memorySession"]=current_load;beat(20)
    fresh=say("The alder tree has a crooked limb.")
    assert any(r["id"]==fresh["id"] for r in view(averil,"conversations"))
    beat(21)
    until(lambda:sql(f"""SELECT count(*) FROM dialogue_recap_parts WHERE source='{fish["id"]}'""")=="0")
    assert sql(f"""SELECT count(*) FROM dialogue_recap_parts WHERE source='{fresh["id"]}'""")!="0"
    assert "speckled" not in texts(check_recall(averil,ask))
    assert "crooked" in texts(check_recall(averil,"What about the crooked limb?"))
    for table in ("turns","cue_events","memory_summaries","resident_recaps","dialogue_day_recaps","dialogue_recap_parts","daily_events","character_knowledge","personality_turning_points"):
        assert sql("SELECT count(*) FROM "+table+" t WHERE to_jsonb(t)::text LIKE ANY(ARRAY['%copper thimble%','%magpie stole%'])")=="0",table
    assert {"fishing","plum tart"}<=values(averil) and "gold" in values(drystan)
    assert any(r["kind"]=="promise" for r in view(averil,"commitments"))
    assert any(r["kind"]=="marked" for r in view(averil,"commitments"))
    assert any(r["kind"]=="moment" for r in view(averil,"commitments"))
    assert view(averil,"daily")
    parent=world["cueHead"]
    def fork():
        tip=str(uuid4())
        api("module/cue",dict(world=dict(world),parent=parent,id=tip,kind="talk",people=[]))
        world["cueHead"]=tip;beat(world["day"])
        return tip
    completed=fork()
    api("commitments/"+pledge+"/fulfill",dict(campaign=campaign,actor=averil),expected=204)
    def pledge_status():return next(r["status"] for r in view(averil,"commitments") if r["id"]==pledge)
    assert pledge_status()=="fulfilled"
    fork()
    assert pledge_status()=="open"
    world["cueHead"]=completed;beat(world["day"])
    assert pledge_status()=="fulfilled"
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
