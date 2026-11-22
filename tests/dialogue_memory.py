"""Verify learning, day boundaries and durable delivery receipts against the actual server and database."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
import time
from uuid import uuid4


FISH_STORY = "I like fishing. Plum tart is my favorite food. We landed a speckled trout beneath the alder footbridge."
FISH_FOLLOWUP = "Which stretch of river would you recommend for angling again? I'd love to go back there."
FISH_CALLBACK = "I've got my rod out again. Remind me, whereabouts did we catch that one together?"
REPAIR_CALLBACK = "I've got my rod out again. How did we fix the alder footbridge?"


def check(api, beat, world, campaign, residents, sql, calls, waiting, release, memory_model):
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

    def native_turn(words):
        packet = dict(campaign=campaign, actor=averil, id=str(uuid4()), text=words, supportsSpokenFarewells=True)
        prepared = api("dialogue/prepare", packet)
        assert prepared["state"] == "ready", prepared
        assert api("dialogue/prepare", packet) == prepared
        dispatches = [call for call in calls if call["messages"][1]["content"] == words]
        assert len(dispatches) == 1, (words, len(dispatches))
        prompt = dispatches[0]["messages"][0]["content"]
        recall = json.JSONDecoder().raw_decode(prompt.split("\nRecall: ", 1)[1])[0].get("conversationRecaps", [])
        answer = api("dialogue/finish", dict(packet, reaction="warm"))
        assert answer["state"] == "complete", answer
        return packet, recall, answer["reply"]

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

    def group(who, words, audience, to="group", chat=None, **extra):
        chat = chat or session
        seq = sum(p["line"]["session"] == chat for p in packets) + 1
        packet = dict(id=str(uuid4()), parent=world.get("cueHead"), world=dict(world), kind="group", people=[],
                      line=dict(session=chat, seq=seq, speaker=who, to=to, words=words,
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
    # At the same table, the same speaker addresses two different people.
    cart_chat = str(uuid4())
    to_resident = group(averil, "You may use my handcart.", both, to=drystan, chat=cart_chat)
    to_player = group(averil, "You may not use my handcart.", both, to="player", chat=cart_chat)
    invite = "Will you come to the solstice supper?"
    invitations = [group(averil, invite, both, to=person) for person in (drystan, "player")]
    bell_chat = str(uuid4())
    bell = group(averil, "I carried the pewter bell to Reed Quay.", [*both, guest], chat=bell_chat)
    revised_bell = group(averil, "I was mistaken. Ada carried it.", both, chat=bell_chat)
    supper_chat = str(uuid4())
    group(averil, "You are invited to my harvest supper.", [*both, guest], to=guest, chat=supper_chat)
    sundial = group("player", "I painted the copper sundial.", [*both, guest])
    revised_sundial = private(guest, "I was mistaken. Rowan painted it.")
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
    fish, _, _ = native_turn(FISH_STORY)
    repair=say("We replaced the broken planks at the alder footbridge.")
    say("We went fishing from the stone pier.")
    say("The orchard bees swarmed beside the tumbledown chapel.")
    craft=say("The glassblower shaped a blue goblet at the bell tower.")
    lens=say("I may have left an obsidian lens beneath Drift Hall. Did the courier find it?")
    journey=say("Nia moved amber from Willow Hall to Larch Quay.")
    borrow_self = say("May I borrow your cart?")
    borrow_other = say("May he borrow my cart?")
    hearsay = say("Mara doubts he lent me his compass.")
    denial = say("I never told him he could use my skiff.")
    denials = [say(s, guest) for s in ("I did not say you could borrow my horse.", "I said you did not borrow my horse.")]
    modal = [say(s, guest) for s in ("Mira might mend Elm Bridge.", "Mira did mend Elm Bridge.")]
    nested = say('Jo reported “I was mistaken. Mira may not take my garnet.”', guest)
    sash = say("I wove the russet sash at the guildhall.", guest)
    condition = say("You may borrow my spyglass for the journey across the valley tomorrow unless my sister needs it "
                    "to count the wild cranes resting behind the warm orchard wall at sunrise.", guest)
    ring = say("Mira, said Jo, took the ring.", guest)
    foal = say("I sold the foal.", guest)
    trout = say("I landed a speckled trout beneath the alder footbridge. I was mistaken. Ada caught it.", guest)
    pike = say("I caught a huge pike at the mill pond. Starlings circled the cracked granary roof. "
               "Frost coated three empty copper buckets. Violet ribbons marked nine broken gates. Well, no, I didn’t.", guest)
    assert any(r["id"]==fish["id"] for r in view(averil,"conversations"))
    missing=str(uuid4())
    api("module/cue",dict(world=dict(world),kind="address",parent=full_head,id=missing,
        people=[dict(owner="Recap-NPC",nativeId="late-roster",description="The bronze weathercock rattled above the granary.")]))
    full_head=missing;world["cueHead"]=full_head;beat(20)
    quest=str(uuid4())
    base=dict(key="native-whistle",campaign=campaign,actor=averil,session=quest)
    offer=dict(key="native-whistle",title="An errand",terms="Bring flour to the mill.",objectives=["Collect flour"])
    root=dict(base,id=quest,offer=offer)
    assert api("quests/turn",root)["state"]=="ready"
    assert api("quests/finish",dict(base,id=quest,action="continue"))["state"]=="complete"
    chosen=dict(base,id=str(uuid4()),parent=quest,text="I like rye bread. I accept.")
    assert api("quests/turn",chosen)["action"]=="accept"
    ack=dict(base,id=chosen["id"],action="accept")
    assert api("quests/finish",ack)["state"]=="complete"
    # A native cue can arrive before the next day's heartbeat.
    early=str(uuid4())
    early_chat=str(uuid4())
    api("module/cue",dict(id=early,parent=full_head,world=dict(world,day=21),kind="group",people=[],
        line=dict(session=early_chat,seq=1,speaker="player",to="group",audience=both,words="The clay oven is warm.",
                  at=datetime.now(timezone.utc).isoformat())))
    expiring=say("Could you bring the copper thimble to the mill?")
    delayed=dict(id=str(uuid4()),campaign=campaign,actor=averil,text="Would you do that for me?")
    with ThreadPoolExecutor(max_workers=1) as pool:
        future=pool.submit(api,"dialogue/prepare",delayed)
        try:
            assert waiting.wait(5),"The provider did not receive the delayed reply"
            prompt=next(call["messages"][0]["content"] for call in reversed(calls) if call["messages"][1]["content"]==delayed["text"])
            assert expiring["text"] in prompt
            beat(21)
            assert sql("SELECT dialogue_expired FROM turns WHERE id='"+expiring["id"]+"'")=="t"
            assert any(r["id"]=="day-20" and r.get("hasOriginal") is False for r in view(averil,"sessions"))
        finally:release.set()
        assert future.result()["state"]=="ready"
    assert api("dialogue/finish",dict(delayed,reaction="neutral"))["state"]=="complete"
    assert sql(f"""SELECT count(*) FROM commitments WHERE turn_id='{delayed["id"]}'""")=="0"
    assert "copper thimble" not in sql(f"""SELECT talk FROM turns WHERE id='{delayed["id"]}'""")
    beat(21)
    assert sql(f"""SELECT dialogue_expired FROM turns WHERE id='{delayed["id"]}'""")=="t"
    print("PASS a proposal retired during generation cannot return through a pending reply.",flush=True)
    def check_recall(actor,query):
        say(query,actor)
        prompt=next(call["messages"][0]["content"] for call in reversed(calls) if call["messages"][1]["content"]==query)
        return json.loads(prompt.split("\nRecall: ",1)[1]).get("conversationRecaps",[])
    def texts(items):return " ".join(r["note"].lower() for r in items)
    def notes_by_source(words, expected):
        notes = check_recall(averil, words)
        print("Compacted roles", json.dumps(dict(query=words, recaps=notes)), flush=True)
        assert {r["source"] for r in notes} == set(expected), notes
        return {r["source"]: r for r in notes}

    carts = notes_by_source("Who asked to borrow a cart?", [borrow_self["id"], borrow_other["id"]])
    own_request, other_request = (carts[p["id"]] for p in (borrow_self, borrow_other))
    assert own_request["speakerKey"] == other_request["speakerKey"] == "player"
    assert own_request["toKey"] == other_request["toKey"] == averil
    assert "the speaker may borrow the addressee's cart" in own_request["note"], own_request
    assert "he may borrow the speaker's cart" in other_request["note"], other_request
    for source in (borrow_self, borrow_other, hearsay, denial):
        assert sql(f"""SELECT player_text='' AND reply IS NULL FROM turns WHERE id='{source["id"]}'""") == "t"
    compass = notes_by_source("Did anybody mention a compass?", [hearsay["id"]])[hearsay["id"]]
    assert "mara doubts" in compass["note"].lower() and "his compass" in compass["note"].lower(), compass
    skiff = notes_by_source("What about the skiff?", [denial["id"]])[denial["id"]]
    assert "never told him" in skiff["note"] and "could use" in skiff["note"], skiff
    for packet, query, detail in [(trout, "Who caught the speckled trout?", "Ada caught it"),
                                  (pike, "Who caught the huge pike at the mill pond?", "Withdrew")]:
        recalled = check_recall(guest, query)
        print("Corrected account", json.dumps(recalled), flush=True)
        original = next(r for r in recalled if r["source"] == packet["id"])
        assert "later corrected" in original["note"] and detail in original["correction"]["note"], original
        assert original["correction"]["source"] == packet["id"] and original["correction"]["sentence"] > 0, original
    for packet in (ring, foal):
        until(lambda: sql(f"""SELECT highlighted AND journal_note IS NULL FROM dialogue_recap_parts
                              WHERE source='{packet["id"]}' AND pos=1""") == "t")
    ring_note = check_recall(guest, "Who took the ring?")[0]
    assert ring_note["source"] == ring["id"] and "Mira, said Jo," in ring_note["note"], ring_note
    highlighted = next(r["body"] for r in view(guest,"sessions") if r["id"]=="day-20")
    assert ring_note["note"] in highlighted and "Reported Mira said Jo took" not in highlighted, highlighted
    foal_note = check_recall(guest, "What became of the foal?")[0]
    assert foal_note["source"] == foal["id"] and foal_note["note"] in highlighted and "Suggested" not in highlighted, highlighted
    print("Protected journal highlights", json.dumps(dict(ring=ring_note,foal=foal_note,journal=highlighted)), flush=True)
    def pair(packet_pair, query):
        notes = {r["source"]: r["note"] for r in check_recall(guest, query)}
        return [notes[p["id"]] for p in packet_pair]
    refused_to_say, reported_denial = pair(denials, "Who mentioned a horse?")
    assert "did not say" in refused_to_say and "did not borrow" in reported_denial
    possible, asserted = pair(modal, "What happened at Elm Bridge?")
    assert "Mira might mend" in possible and "Mira did mend" in asserted
    reported = check_recall(guest, "What about the garnet?")[0]
    assert reported["source"] == nested["id"] and "Jo reported" in reported["note"] and "Mira may not" in reported["note"], reported
    assert not reported.get("correction") and sql(f"""SELECT count(*) FROM dialogue_recap_parts
        WHERE source='{nested["id"]}' AND pos=1""") == "1"
    partial = check_recall(guest, "Tell me about the spyglass.")[0]
    assert partial["source"] == condition["id"] and "Partial" in partial["note"] and "unless" in partial["note"], partial
    supper = next(r for r in view(drystan, "sessions") if r["id"] == "day-20-" + supper_chat)
    assert "Miller to Listener — " in supper["body"] and "harvest supper" in supper["body"], supper
    def day_recap(actor):
        return next(r for r in view(actor,"sessions") if r["id"]=="day-20")
    assert sum(r["id"]=="day-20" for r in view(averil,"sessions"))==1
    assert api("quests/turn",chosen)["state"]=="expired" and api("quests/turn",root)["state"]=="expired"
    assert api("quests/finish",ack)["state"]=="expired"
    api("quests/turn",dict(root,offer=dict(offer,terms="Different terms.")),expected=409)
    api("quests/turn",dict(chosen,text="Changed."),expected=409)
    api("quests/finish",dict(ack,action="decline"),expected=409)
    assert "rye bread" in values(averil)
    assert sql(f"""SELECT cardinality(audience) FROM dialogue_recap_parts WHERE anchor='{missing}'""")=="0"
    identified=uuid4().hex
    sql(f"""INSERT INTO actors SELECT campaign,'{identified}','Recap-NPC','late-roster','Wren',kind,job,town,true,raw
        FROM actors WHERE campaign='{campaign}' AND key='{averil}'""")
    api("module/cue",dict(world=dict(world),kind="talk",people=[],id=str(uuid4()),parent=full_head))
    assert "granary" in day_recap(identified)["body"].lower()
    assert "weathercock" not in day_recap(averil)["body"].lower()
    world["cueHead"]=early;beat(21)
    assert api(prefix+averil+"/history/sessions/"+early_chat)[0]["text"]=="The clay oven is warm."
    world["cueHead"]=full_head;beat(21)
    assert day_recap(averil)["visits"]>=2 and day_recap(averil)["hasOriginal"] is False,day_recap(averil)
    api(prefix+averil+"/history/sessions/day-20",expected=404)
    api(prefix+averil+"/sources/turn/"+fish["id"],expected=404)
    ask="Which quiet stretch of river would you recommend for angling?"
    remembered=check_recall(averil,ask)
    assert "alder footbridge" in texts(remembered) and "broken planks" not in texts(remembered),remembered
    assert any(r["speaker"]=="Test player" and r["day"]==20 for r in remembered)
    native_turn(FISH_FOLLOWUP)
    assert "planks" in texts(check_recall(averil,"How are the repairs coming along?"))
    assert "chapel" in texts(check_recall(averil,"Is this a good place for an apiary?"))
    until(lambda: sql(f"""SELECT aliases FROM dialogue_recap_parts WHERE source='{craft["id"]}' AND pos=1""")=="glassmaking stemware")
    assert "bell tower" in texts(check_recall(averil,"Who around here knows about stemware?"))
    until(lambda: sql(f"""SELECT journal_note FROM dialogue_recap_parts WHERE source='{lens["id"]}' AND pos=1 AND sentence=0""")
          == "Reported that they may have left an obsidian lens beneath Drift Hall.")
    assert sql(f"""SELECT journal_note IS NULL FROM dialogue_recap_parts
                   WHERE source='{fish["id"]}' AND pos=1 AND sentence=2 AND aliases='angling'""")=="t"
    qualified=check_recall(averil,"Where did you leave the obsidian lens? The repairs can wait.")
    assert qualified[0]["source"]==lens["id"] and "may have left" in qualified[0]["note"] and "Drift Hall" in qualified[0]["note"],qualified
    route=check_recall(averil,"Did Nia move any amber?")
    assert route[0]["source"]==journey["id"] and "from Willow Hall to Larch Quay" in route[0]["note"],route
    unrelated=check_recall(averil,"Have you seen any emeralds?")
    assert not unrelated,unrelated
    assert not check_recall(drystan,ask)
    assert sql(f"""SELECT player_text='' AND reply IS NULL FROM turns WHERE id='{fish["id"]}'""")=="t"
    assert not any(r["id"]==old["id"] for r in view(averil,"conversations"))
    # Verify the same visible source boundaries after the group's original words have gone.
    group_entry="day-20-"+session
    def group_body(actor):return next((r["body"] for r in view(actor,"sessions") if r["id"]==group_entry),"")
    assert group_body(drystan) and "magpie" not in group_body(guest).lower()
    def shared_recall(words,listeners):
        chat,tip=str(uuid4()),str(uuid4())
        api("module/cue",dict(world=dict(world),kind="group",people=[],parent=full_head,id=tip,
            line=dict(session=chat,seq=1,speaker="player",to="group",audience=listeners,words=words,at=datetime.now(timezone.utc).isoformat())))
        api("dialogue/group",dict(campaign=campaign,actor=averil,head=tip,session=chat,audience=listeners))
        context=next(json.loads(x["messages"][1]["content"]) for x in reversed(calls) if "heardInOrder" in x["messages"][1]["content"])
        return context["yourMemory"].get("conversationRecaps",[])
    assert "buckle" in texts(shared_recall("What became of the magpie?",both))
    assert not shared_recall("What became of the magpie?",[*both,guest])
    assert not shared_recall(ask,both)
    permissions = shared_recall("What did you say about using the handcart?", both)
    assert {r["source"] for r in permissions} == {to_resident, to_player}, permissions
    grant = next(r for r in permissions if r["source"] == to_resident)
    refusal = next(r for r in permissions if r["source"] == to_player)
    assert grant["speakerKey"] == refusal["speakerKey"] == averil
    assert grant["toKey"] == drystan and refusal["toKey"] == "player", permissions
    assert "the addressee may use the speaker's handcart" in grant["note"], grant
    assert "the addressee may not use the speaker's handcart" in refusal["note"], refusal
    cart_journal = next(r["body"] for r in view(drystan,"sessions") if r["id"]=="day-20-"+cart_chat)
    assert all(f"{r['speaker']} to {r['to']} — {r['note']}" in cart_journal for r in permissions), cart_journal
    print("Compacted permissions", json.dumps(dict(recaps=permissions,journal=cart_journal)), flush=True)
    assert not shared_recall("What did you say about using the handcart?", [*both, guest])
    invited = shared_recall("Who was invited to the solstice supper?", both)
    assert {(r["source"], r["toKey"]) for r in invited} == set(zip(invitations, (drystan, "player"))), invited
    # Simulate an old receipt without a recorded target. Both witnesses stay in the audience.
    sql(f"""UPDATE cue_events SET payload=payload #- '{{line,to}}' WHERE id='{invitations[0]}'""")
    unaddressed = next(r for r in shared_recall("Who was invited to the solstice supper?", both) if r["source"] == invitations[0])
    assert not unaddressed.get("to") and not unaddressed.get("toKey"), unaddressed
    where_bell = "Who carried the pewter bell to Reed Quay?"
    def bell_note(listeners):
        return next(r for r in shared_recall(where_bell, listeners) if r["source"] == bell)
    changed = bell_note(both)
    assert changed["correction"]["source"] == revised_bell and "Ada carried it" in changed["note"], changed
    original_bell = bell_note([*both, guest])
    assert "correction" not in original_bell
    def bell_journal(actor):
        return next(r["body"] for r in view(actor,"sessions") if r["id"]=="day-20-"+bell_chat)
    assert changed["note"] in bell_journal(averil) and "Ada" not in bell_journal(guest)
    sundial_query = "Who painted the copper sundial?"
    heard = next(r for r in check_recall(guest, sundial_query) if r["source"] == sundial)
    assert heard["correction"]["source"] == revised_sundial and "Rowan painted it" in heard["note"], heard
    public = next(r for r in shared_recall(sundial_query, [*both, guest]) if r["source"] == sundial)
    assert "correction" not in public and "Rowan" not in public["note"], public
    latest = full_head
    full_head = bell
    world["cueHead"] = bell;beat(21)
    rewound_bell = bell_note(both)
    assert "correction" not in rewound_bell and "Ada" not in bell_journal(averil)
    full_head = latest
    world["cueHead"] = latest;beat(21)
    print("Witnesses and rewind", json.dumps(dict(revised=changed,shared=original_bell,rewound=rewound_bell,
          journal=bell_journal(averil),absentJournal=bell_journal(guest),privateCorrection=heard,publicAccount=public)), flush=True)
    new_sash = say("I was mistaken. Emil wove it.", guest)
    world["cueHead"]=packets[0]["id"];beat(21)
    assert group_body(drystan) and not group_body(guest)
    world["cueHead"]=full_head;beat(21)
    # Two children of the same save must never share a refinement prompt, even with identical listeners.
    for token in ("spindle whorl","silver brooch"):
        tip=str(uuid4())
        api("module/cue",dict(world=dict(world),kind="group",people=[],id=tip,parent=full_head,
            line=dict(session=early_chat,seq=1,speaker="player",to="group",audience=both,words="I found a "+token+".",
                      at=datetime.now(timezone.utc).isoformat())))
    beat(22)
    revised_sash = next(r for r in check_recall(guest, "Tell me about the russet sash.") if r["source"] == sash["id"])
    assert revised_sash["correction"]["source"] == new_sash["id"] and "Emil wove it" in revised_sash["note"], revised_sash
    print("Later day", json.dumps(revised_sash), flush=True)
    def model_batches():return [call["messages"][1]["content"] for call in calls if "Summarize one day of spoken dialogue" in call["messages"][0]["content"]]
    until(lambda:all(any(token in batch for batch in model_batches()) for token in ("spindle whorl","silver brooch")))
    assert not any("spindle whorl" in batch and "silver brooch" in batch for batch in model_batches())
    beat(49)
    assert day_recap(averil) and "alder" in texts(check_recall(averil,ask))
    _, recalled, reply = native_turn(FISH_CALLBACK)
    print("Day-30 native provider replay", json.dumps(dict(recaps=recalled, reply=reply)), flush=True)
    assert recalled and recalled[0]["speaker"] == "Test player" and recalled[0]["speakerKey"]=="player" and recalled[0]["source"] == fish["id"], recalled
    assert recalled[0]["day"] == 20 and "landed a speckled trout beneath the alder footbridge" in recalled[0]["note"], recalled
    assert "broken planks" not in texts(recalled) and "alder footbridge" in reply, (recalled, reply)
    _, repairs, reply = native_turn(REPAIR_CALLBACK)
    assert repairs and repairs[0]["source"] == repair["id"] and "alder footbridge" in reply, (repairs, reply)
    assert "speckled trout" not in texts(repairs), repairs
    print("Same-bridge repair", json.dumps(dict(recaps=repairs, reply=reply)), flush=True)
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
    assert sql(f"""SELECT count(*) FROM dialogue_recap_parts WHERE source='{sash["id"]}'""") == "0"
    assert sql(f"""SELECT detail FROM dialogue_recap_parts WHERE source='{new_sash["id"]}' AND pos=1""").endswith("Emil wove it”.")
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
    assert any(r["value"].casefold()=="plum tart" and r["saidBy"]=="Test player" for r in knowledge),knowledge
    print("PASS 30-day physical expiry, reload clock, preserved new branch speech, group witnesses, short answers, corrections, exact/conflicting replays, durable preferences, commitments and major moments.",flush=True)

    # Closed groups each have their own journal entry. Read the same evidence through
    # private native Talk after the overnight model has actually failed.
    beat(100)
    memory_model("offline-night")
    def exchange(lines):
        chat = str(uuid4())
        sources = [group(who, words, both, to=to, chat=chat) for who, to, words in lines]
        return chat, sources
    rings = exchange([("player", averil, "Mira said I stole her ring."), ("player", averil, "I said Mira stole my ring.")])
    trout_words = "I landed a speckled trout beneath the alder footbridge. I was mistaken. Ada caught it."
    fixed = exchange([("player", averil, trout_words)])
    five = exchange([("player", averil, pike["text"])])
    stool = exchange([(averil, drystan, "You moved my stool."), (averil, "group", "You moved my stool.")])
    secondhand = exchange([("player", averil, "I repaired the flax press."), (averil,"player","Mira said she was mistaken about the flax press.")])
    ambiguous = exchange([("player", averil, "I sold a shawl. I sold a cloak. I was mistaken.")])
    clarified = exchange([("player", averil, "I strung the red lantern. Actually, Ada strung it.")])
    assent_chat, _ = exchange([("player", averil, "Yes?"), (averil, "player", "No.")])
    beat(101)
    until(lambda: any(call["model"] == "offline-night" and "I landed a speckled trout" in call["messages"][1]["content"] for call in calls))
    assent = next(r["body"] for r in view(averil,"sessions") if r["id"] == "day-100-"+assent_chat)
    assert "Asked “Yes?”" in assent and "Answered no." in assent and "Answered yes." not in assent, assent
    def concordance(scene, question, day=100):
        chat, sources = scene
        _, selected, reply = native_turn(question)
        notes = [r for r in selected if r["source"] in sources]
        body = next(r["body"] for r in view(averil,"sessions") if r["id"] == f"day-{day}-{chat}")
        assert {r["source"] for r in notes} == set(sources) and all(r["note"] in body for r in notes), (notes,body)
        assert selected[0]["source"] in sources and reply == selected[0]["note"], (reply,selected)
        print("Talk and journal", json.dumps(dict(query=question,recaps=selected,reply=reply,journal=body)), flush=True)
        return notes
    claims = concordance(rings, "What did we say about stealing the ring?")
    assert "mira said i stole her ring" in texts(claims) and "i said mira stole my ring" in texts(claims), claims
    assert "Ada caught it" in concordance(fixed, "Who landed that speckled trout?")[0]["correction"]["note"]
    assert "Withdrew" in concordance(five, "Did anyone catch a huge pike?")[0]["correction"]["note"]
    seats = concordance(stool, "Who moved whose stool?")
    assert {r["toKey"] for r in seats} == {drystan, "group"} and any("You moved my stool" in r["note"] for r in seats if r["toKey"]=="group")
    assert not any(r.get("correction") for r in concordance(secondhand, "What did we say about the flax press?"))
    unclear = concordance(ambiguous, "Who sold the shawl?")[0]
    assert "unclear target" in unclear["note"] and unclear["correction"]["targetUnclear"]
    assert "Ada strung it" in concordance(clarified, "Who strung the red lantern?")[0]["correction"]["note"]

    beat(140)
    single = exchange([("player", averil, "You may borrow my cart. I was mistaken about the harvest.")])
    separate, (sled, _) = exchange([("player", averil, "You may borrow my sled."),
                                     ("player", averil, "I was mistaken about the orchard.")])
    harvest = exchange([(averil, "player", "I counted three harvest wagons. You may borrow my kettle. "
                                          "I was mistaken about the harvest. The owls were loud.")])
    sacks, (weighed, basket, amended) = exchange([(averil, "player", "I weighed the harvest sacks."),
        (averil, "player", "You may borrow my basket."), (averil, "player", "I was mistaken about the harvest sacks.")])
    hamper, (carried, withdrew) = exchange([("player", averil, "I carried the wicker hamper."),
                                             ("player", averil, "I take that back.")])
    unrelated = exchange([("player", averil, "I lent Ada my chest. Bo lost his purse. I was wrong about the barley.")])
    corrected = exchange([("player", averil, "I landed a speckled trout beneath the alder footbridge. "
                                            "I was mistaken about who caught it. Ada caught it.")])
    paint_chat = str(uuid4())
    painted = group(averil,"I painted the red handcart.",[*both,guest],chat=paint_chat)
    first_painter = group(averil,"I was mistaken. Nia painted it.",[*both,guest],chat=paint_chat)
    last_painter = group(averil,"I was mistaken. Bram painted it.",both,chat=paint_chat)
    stamps, (brass, silver, uncertain) = exchange([("player", averil, "I stamped the brass seal."),
        ("player", averil, "I stamped the silver coin."), ("player", averil, "I was mistaken. Finn stamped it.")])
    beat(141)
    for scene, noun in [(single,"cart"), ((separate,[sled]),"sled"), (harvest,"kettle"), ((sacks,[basket]),"basket")]:
        notes = concordance(scene, f"What permission was given to borrow the {noun}?", 140)
        grant = next(r for r in notes if "may borrow" in r["note"])
        assert "correction" not in grant and grant["note"] == f"Said the addressee may borrow the speaker's {noun}.", grant
    wagons = concordance(harvest, "Who counted the three harvest wagons?", 140)[0]
    assert wagons["sentence"]==0 and wagons["correction"]["sentence"]==2 and "owls" not in wagons["note"], wagons
    assert "owls" in concordance(harvest, "What did you hear from the owls?",140)[0]["note"]
    weighed_note = concordance((sacks,[weighed]), "Who weighed the harvest sacks?", 140)[0]
    assert weighed_note["correction"]["source"]==amended, weighed_note
    hamper_note = concordance((hamper,[carried]), "Who carried the wicker hamper?", 140)[0]
    assert hamper_note["correction"]["source"]==withdrew, hamper_note
    chest = concordance(unrelated, "Who lent Ada a chest?", 140)[0]
    assert "correction" not in chest and "lent Ada" in chest["note"], chest
    catch = concordance(corrected, "Who actually landed the speckled trout at the footbridge?", 140)[0]
    assert "Ada caught it" in catch["correction"]["note"], catch
    for source,item in [(brass,"brass seal"),(silver,"silver coin")]:
        stamped = concordance((stamps,[source]),f"What did I say about the {item}?",140)[0]
        assert "correction" not in stamped, stamped
    assert sql(f"SELECT corrects_source IS NULL FROM dialogue_recap_parts WHERE source='{uncertain}'")=="t"
    paint_query = "Who painted the red handcart?"
    last = concordance((paint_chat,[painted]), paint_query, 140)[0]
    assert last["correction"]["source"]==last_painter and "Bram painted it" in last["note"] and "Nia" not in last["note"], last
    def paint_journal(actor):
        return next(r["body"] for r in view(actor,"sessions") if r["id"]=="day-140-"+paint_chat)
    previous = next(r for r in check_recall(guest, paint_query+" I missed the end.") if r["source"]==painted)
    assert previous["correction"]["source"]==first_painter and "Nia painted it" in previous["note"], previous
    assert previous["note"] in paint_journal(guest) and "Bram" not in paint_journal(guest)
    latest_head=world["cueHead"]
    world["cueHead"]=first_painter; beat(141)
    rewound = next(r for r in check_recall(averil, paint_query+" Remind me.") if r["source"]==painted)
    assert rewound["correction"]["source"]==first_painter and rewound["note"] in paint_journal(averil) and "Bram" not in paint_journal(averil)
    print("Successive corrections",json.dumps(dict(latest=last,partialWitness=previous,rewound=rewound,
          partialJournal=paint_journal(guest),rewoundJournal=paint_journal(averil))),flush=True)
    world["cueHead"]=latest_head; beat(141)

    beat(175)
    quotes = ["“May I borrow your cart?” Jo asked.", "Jo asked, “May I borrow your cart?”"]
    speakers = exchange([("player",averil,words) for words in quotes])
    _, (distinct,) = exchange([("player",averil,"“The well is dry.” Jo opened the gate.")])
    beat(176)
    tagged = concordance(speakers, "Who asked about borrowing a cart?", 175)
    for words, source in zip(quotes, speakers[1]):
        note = next(r["note"] for r in tagged if r["source"]==source)
        assert words.rstrip(".") in note, note
        assert sql(f"SELECT count(*) FROM dialogue_recap_parts WHERE source='{source}'")=="1"
    assert sql(f"SELECT count(*) FROM dialogue_recap_parts WHERE source='{distinct}'")=="2"
    beat(210)
    busy = exchange([("player",averil,quotes[0]+" Ravens circled the red quarry. Miners carried copper ore. Pine martens crossed the stream.")])
    warning = "“Don't lend him my cart!” Jo warned."
    refusal = exchange([("player",averil,warning)])
    beat(211)
    assert quotes[0].rstrip(".") in concordance(busy,"What was Jo asking to borrow?",210)[0]["note"]
    assert warning.rstrip(".") in concordance(refusal,"Who warned me against lending him the cart?",210)[0]["note"]
    print("PASS named corrections qualify only their evidenced subject, and quoted questions retain their reporting clause.", flush=True)
    beat(245)
    grant = exchange([("player",averil,"You may borrow my cart. I take that back. I have to go.")])
    refusal = exchange([("player",averil,"You may not borrow my saddle. I take that back. The kettle is boiling.")])
    maybe, (cloak, _) = exchange([("player",averil,"I might lend you my cloak."),
                                  ("player",averil,"I take that back. The lantern has gone out.")])
    across, (wagon, _) = exchange([("player",averil,"You may borrow my wagon."),("player",averil,"I take that back. I have to go.")])
    actor_only = exchange([("player",averil,"I landed a speckled trout beneath the alder footbridge. I was mistaken. It was Ada.")])
    painter, (painted, _) = exchange([("player",averil,"I painted the red handcart."),("player",averil,"I was mistaken. It was Nia.")])
    named_actor = exchange([("player",averil,"I polished the bronze buckle. I was mistaken about who polished it. It was Nell.")])
    pottery = exchange([("player",averil,"I glazed the stoneware jug.")])
    _, (potter,) = exchange([("player",averil,"I was mistaken. Nell glazed the jug.")])
    plain = exchange([("player",averil,"You may borrow my mirror. I take that back.")])
    unclear_grant = exchange([("player",averil,"You may borrow my spade. You may borrow my rake. I take that back. I have to go.")])
    tools_chat = str(uuid4())
    hoe = group("player","You may borrow my hoe.",[*both,guest],to=averil,chat=tools_chat)
    pitchfork = group("player","You may borrow my pitchfork.",[*both,guest],to=averil,chat=tools_chat)
    withdrawal = group("player","I take that back. I have to go.",both,to=averil,chat=tools_chat)
    beat(246)
    for scene, word, original in [(grant,"cart","may borrow"), (refusal,"saddle","may not borrow"),
                                   ((maybe,[cloak]),"cloak","might lend"), ((across,[wagon]),"wagon","may borrow"),
                                   (plain,"mirror","may borrow")]:
        note = concordance(scene, f"What did you say about lending the {word}?",245)[0]
        assert original in note["note"] and note["correction"]["note"]=="Withdrew their earlier account.", note
    assert "kettle" in concordance(refusal,"What was boiling?",245)[0]["note"]
    pot = concordance(pottery,"Who glazed the stoneware jug?",245)[0]
    assert pot["correction"]["source"]==potter and "Nell glazed the jug" in pot["note"], pot
    for scene, query, name in [(actor_only,"Who landed the trout beneath the bridge?","Ada"),
                               ((painter,[painted]),"Who actually painted the handcart?","Nia"),
                               (named_actor,"Who polished the bronze buckle?","Nell")]:
        note = concordance(scene,query,245)[0]
        assert f"It was {name}" in note["correction"]["note"], note
    for scene, tool in [(unclear_grant,"spade"),((tools_chat,[hoe]),"hoe"),((tools_chat,[pitchfork]),"pitchfork")]:
        note = concordance(scene,f"What permission did I give about the {tool}?",245)[0]
        amendment = note.get("uncertainty") or note.get("correction")
        assert "unclear target" in note["note"] and amendment["targetUnclear"], note
    def original_permission(listener, query):
        note = next(r for r in check_recall(listener,query) if r["source"]==hoe)
        body = next(r["body"] for r in view(listener,"sessions") if r["id"]=="day-245-"+tools_chat)
        assert note["note"] in body and not ({"correction","uncertainty"} & note.keys()) and "Withdrew" not in body, (note,body)
        return dict(recap=note,journal=body)
    partial = original_permission(guest,"What did the player say about the hoe?")
    world["cueHead"]=pitchfork; beat(246)
    rewind = original_permission(averil,"What did I tell you about the hoe?")
    print("Withdrawal witnesses",json.dumps(dict(withdrawal=withdrawal,partialWitness=partial,rewound=rewind)),flush=True)
    world["cueHead"]=withdrawal; beat(246)
    print("PASS a bare withdrawal survives the next sentence, actor-only replacements stay attributed, and ambiguous withdrawals qualify possible permissions.",flush=True)

    beat(280)
    bell_scene = exchange([("player",averil,"I caught a pike by the mill. You may borrow my lantern. "
                                            "I take that back. It is the mill bell ringing.")])
    yard_scene = exchange([("player",averil,"I carved the bowl in the old stone yard. You may use my awl. "
                                            "I was mistaken. The old stone yard is empty.")])
    beat(281)
    for scene, item in [(bell_scene,"pike"),(bell_scene,"lantern"),(yard_scene,"bowl"),(yard_scene,"awl")]:
        note = concordance(scene,f"What did I tell you about the {item}?",280)[0]
        assert note["correction"]["targetUnclear"] and "unclear target" in note["note"], note
    bell_note = concordance(bell_scene,"What was ringing at the mill?",280)[0]
    assert bell_note["sentence"]==3 and "correction" not in bell_note, bell_note

    for day, mixed in [(315,False),(350,True)]:
        beat(day)
        previous = exchange([("player",averil,"I painted the red handcart.")])
        grant = "You may borrow my fishing rod."
        amendment = "I was mistaken. It was Nia who painted the red handcart."
        visit, ids = exchange([("player",averil,text) for text in ([grant+" "+amendment] if mixed else [grant,amendment])])
        beat(day+1)
        query = "Who painted the red handcart in that visit?" if mixed else "Who painted my old red handcart?"
        paint = concordance(previous,query,day)[0]
        assert paint["correction"]["source"]==ids[-1] and "Nia who painted the red handcart" in paint["note"], paint
        query = "Did you let me borrow the fishing rod in that visit?" if mixed else "What did we say about lending the fishing rod?"
        rod = concordance((visit,[ids[0]]),query,day)[0]
        assert rod["note"]=="Said the addressee may borrow the speaker's fishing rod." and "correction" not in rod, rod

    beat(385)
    sail = exchange([("player",averil,"I stitched the torn sail.")])
    missing = exchange([("player",averil,"You may use my shovel. I was mistaken. It was Nell who stitched the quilt.")])
    needlework = exchange([("player",averil,"I stitched the hood. I stitched the blue banner. I was mistaken. Mara stitched the blue banner.")])
    beat(386)
    shovel = concordance(missing,"Who may use the shovel?",385)[0]
    assert shovel["note"]=="Said the addressee may use the speaker's shovel." and "correction" not in shovel, shovel
    assert "correction" not in concordance(sail,"Who stitched the torn sail?",385)[0]
    banner = concordance(needlework,"Who stitched the blue banner?",385)[0]
    assert banner["sentence"]==1 and "Mara stitched the blue banner" in banner["correction"]["note"], banner
    beat(420)
    painted_cart = exchange([("player",averil,"I painted the red handcart for the cooper.")])
    visit, (shutters,corrected) = exchange([("player",averil,"I painted the shutters beside the red handcart."),
                                            ("player",averil,"I was mistaken. It was Nia who painted the red handcart.")])
    beat(421)
    cooper = concordance(painted_cart,"What did I paint for the cooper?",420)[0]
    assert cooper["correction"]["source"]==corrected and "Nia" in cooper["note"], cooper
    assert "correction" not in concordance((visit,[shutters]),"Who painted the shutters?",420)[0]
    print("PASS incidental scenery preserves uncertainty, and a repeated action selects its source across visits and inline grants.",flush=True)

    beat(455)
    cart = exchange([("player",averil,"I painted the red handcart for the wheelwright.")])
    visit, (shutters,marker) = exchange([("player",averil,"I painted the shutters beside the red handcart."),
                                        ("player",averil,"I was mistaken about who painted the red handcart. It was Nia.")])
    hinge = exchange([("player",averil,"I mended the latch beside the copper hinge. I was wrong regarding who mended the copper hinge. It was Nell.")])
    apron = exchange([("player",averil,"I stitched the green apron. I stitched cuffs beside the green apron. I meant that Mara stitched the green apron.")])
    loan = exchange([("player",averil,"You may borrow my sledge. I take that back. I need it for the harvest.")])
    later, (barrow,reason) = exchange([("player",averil,"You may borrow my barrow."),
                                      ("player",averil,"I take that back. Jo needs my barrow for the orchard.")])
    ordinary = exchange([("player",averil,"I meant every word of the greeting.")])
    beat(456)
    amended = concordance(cart,"What did I paint for the wheelwright?",455)[0]
    assert amended["correction"]["source"]==marker and "It was Nia" in amended["note"], amended
    assert "correction" not in concordance((visit,[shutters]),"What did I tell you about the shutters?",455)[0]
    latch = concordance(hinge,"Who mended the latch?",455)[0]
    assert latch["sentence"]==0 and "correction" not in latch, latch
    orphan = next(n for n in concordance(hinge,"Who mended the copper hinge?",455) if n["sentence"]==1)
    assert "Unclear target." in orphan["note"] and "It was Nell" in orphan["note"], orphan
    seam = concordance(apron,"Who actually stitched the green apron?",455)[0]
    assert seam["sentence"]==0 and "Mara stitched the green apron" in seam["correction"]["note"], seam
    cuffs = concordance(apron,"Who stitched cuffs beside the green apron?",455)[0]
    assert cuffs["sentence"]==1 and "correction" not in cuffs, cuffs
    for scene, noun in [(loan,"sledge"),((later,[barrow]),"barrow")]:
        grant = concordance(scene,f"Did I lend you my {noun}?",455)[0]
        assert grant["correction"]["note"]=="Withdrew their earlier account.", grant
    why = concordance((later,[reason]),"What does Jo need for the orchard?",455)[0]
    assert why["sentence"]==1 and "Jo needs" in why["note"] and "correction" not in why, why
    greeting = concordance(ordinary,"What did I mean by the greeting?",455)[0]
    assert greeting["note"]=="Said they meant every word of the greeting.", greeting
    print("PASS named actions distinguish objects from landmarks, and a withdrawal and its reason keep their separate sources.",flush=True)
    memory_model("historian")
    print("PASS Talk and journal agree on sourced roles, recipients, corrections and unresolved claims after an unavailable overnight model.", flush=True)
