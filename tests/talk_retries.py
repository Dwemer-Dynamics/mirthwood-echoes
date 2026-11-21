from concurrent.futures import ThreadPoolExecutor
import json
from threading import Event


def check_talk_retries(api, model, say, twin, checked, legacy_packet):
    prepare="/api/dialogue/prepare"
    finish="/api/dialogue/finish"
    def current_turn(text):
        return dict(say(text),supportsSpokenFarewells=True)
    before=len(model.calls)
    settings=api("/api/options")
    assert settings["positiveDesireCostPercent"]==25
    for invalid in (0,9,101,None,"unknown"):
        api("/api/options",dict(settings,positiveDesireCostPercent=invalid),"PUT",400)
    turn=current_turn("Would you teach me to fish?")
    answer={"topic":"Leisure","warm":"I will teach you to fish tomorrow.","neutral":"I fish sometimes.",
            "cool":"I prefer to fish alone.","farewells":{"warm":"Take care, friend.","neutral":"Be well.","cool":"Good day."},
            "commitments":[{"speaker":"npc","quote":"I will teach you to fish tomorrow."}]}
    key=turn["text"]
    model.samples[key]=json.dumps(answer)
    model.mode="stream"
    started, release = Event(), Event()
    model.gates[key] = (started, release)
    coat={"name":"Alden","familiarity":"Friend","renown":"famous","standing":"ill-regarded",
          "title":"The Infamous","visibleClothing":{"torso":"Festival Coat"},"newlySeen":["torso"]}
    mail=dict(coat,standing="well-regarded",title="The Paragon",visibleClothing={"torso":"Iron Mail"})
    with ThreadPoolExecutor(5) as pool:
        original=pool.submit(api,prepare,dict(turn,socialDesire=.85,playerImpression=coat))
        try:
            assert started.wait(10), "The fixture never received the original turn."
            pending=list(pool.map(lambda _:api(prepare,dict(turn,socialDesire=.85,playerImpression=mail)),range(4)))
            assert all(p["state"]=="pending" for p in pending)
            api("/api/options",dict(settings,positiveDesireCostPercent=70),"PUT",204)
        finally:
            release.set()
            del model.gates[key]
        prepared=original.result()
    assert prepared["state"]=="ready" and prepared["positiveDesireCostPercent"]==25
    api("/api/options",{k:v for k,v in settings.items() if k!="positiveDesireCostPercent"},"PUT",204)
    assert api("/api/options")["positiveDesireCostPercent"]==70 and api(prepare,turn)["positiveDesireCostPercent"]==25
    assert len(model.calls)==before+1
    prompt=model.calls[-1]["messages"][0]["content"]
    assert 'eager for company' in prompt
    view=json.loads(prompt.split("\nPlayer impression: ")[1])
    assert view=={"relationshipToPlayer":"Friend","knownPlayer":{"name":"Alden","nameRecognition":"famous",
                  "publicStanding":"ill-regarded","publicTitle":"The Infamous"},"visibleClothing":{"torso":"Festival Coat"},"newlySeen":["torso"]}
    api(prepare,dict(turn,actor=twin),status=409)
    api(prepare,dict(turn,text="Different words."),status=409)
    card={"kind":"followup","action":"Leisure","cue":"The resident asks about fishing."}
    with ThreadPoolExecutor(4) as pool:
        results=list(pool.map(lambda _:api(finish,dict(turn,reaction="cool",handoff=card)),range(4)))
    assert all(r["state"]=="complete" and r["reply"]==answer["cool"] and r["handoff"]==card for r in results)
    assert api(finish,dict(turn,reaction="warm"))==results[0] and api(prepare,turn)==results[0]
    assert api(prepare,dict(turn,playerImpression=mail))==results[0] and len(model.calls)==before+1
    path="/api/saves/"+turn["campaign"]+"/characters/"+turn["actor"]+"/history/"
    assert not any(x.get("statement")==answer["warm"] for x in api(path+"commitments")["items"])
    entries=[x for x in api(path+"conversations")["items"] if x["id"]==turn["id"]]
    assert len(entries)==1 and entries[0]["nativeHandoff"]==card["cue"]
    turn=current_turn("Hmm.");model.samples[turn["text"]]=json.dumps(dict(answer,topic="None",commitments=[]))
    prepared=api(prepare,dict(turn,socialDesire=-.85))
    prompt=model.calls[-1]["messages"][0]["content"]
    assert 'very low' in prompt and card["cue"] not in prompt and answer["cool"] in prompt
    assert prepared["state"]=="ready" and prepared["topic"]=="Small Talk" and prepared["positiveDesireCostPercent"]==70
    ending={"kind":"ended","cue":"The resident has had enough conversation."}
    completed=api(finish,dict(turn,reaction="warm",handoff=ending))
    assert completed["handoff"]==ending
    assert completed["reply"]==answer["warm"]+" Take care, friend." and completed["topic"]=="Small Talk" and api(prepare,turn)==completed
    turn=current_turn("*I nod.* Two equal questions: what makes the stars shine, and do you play chess?")
    model.samples[turn["text"]]=json.dumps(dict(answer,topic="None",commitments=[]))
    assert api(prepare,turn)["topic"]=="Small Talk"
    legacy_packet(turn["id"])
    old=api(prepare,turn)
    assert old["positiveDesireCostPercent"]==100 and "endingChoices" not in old
    assert api(finish,dict(turn,reaction="cool",handoff=ending))["reply"]==answer["cool"]
    turn=current_turn("Will you sell me this loaf?");model.samples[turn["text"]]=json.dumps(dict(answer,topic="Other Interaction",commitments=[]))
    assert api(prepare,turn)["topic"]=="None"
    api(finish,dict(turn,reaction="neutral",handoff=card),status=400)
    api(finish,dict(turn,reaction="warm"),status=400)
    assert api(finish,dict(turn,reaction="neutral"))["reply"]==answer["neutral"]
    turn=current_turn("*I study the moon.* *I nod.*");model.samples[turn["text"]]=json.dumps(dict(answer,topic="Intellectual",commitments=[]))
    assert api(prepare,turn)["topic"]=="None"
    api(finish,dict(turn,reaction="cool"),status=400)
    assert api(finish,dict(turn,reaction="neutral"))["reaction"]=="neutral"
    turn=current_turn("This model packet is malformed.");model.samples[turn["text"]]=json.dumps(dict(answer,topic="Forged topic"))
    assert api(prepare,turn)["state"]=="failed" and api(prepare,turn)["state"]=="failed"
    api(finish,dict(turn,reaction="neutral"),status=409)
    assert len(model.calls)==before+6
    old_client=say("Can you tell me what you do for fun?")
    model.samples[old_client["text"]]=json.dumps(answer)
    assert "endingChoices" not in api(prepare,old_client)
    assert "endingChoices" not in api(prepare,dict(old_client,supportsSpokenFarewells=True))
    assert api(finish,dict(old_client,reaction="cool",handoff=ending))["reply"]==answer["cool"]
    check_endings(api,model,current_turn,path,ending)
    check_impressions(api,model,current_turn,answer,coat)
    check_resident_speech(api,model,current_turn,path,answer)
    api("/api/options",settings,"PUT",204)
    checked("social desire settings, older clients, legacy prepared turns and stable per-exchange cost across settings changes")
    checked("parallel Talk retries, scoped UUIDs, grounded promises, Small Talk fallback, silent actions, direct trade and malformed replies")
    checked("spoken farewells on confirmed endings, open and uncertain replies, duplicate farewells, trailing questions and promises")
    checked("resident knowledge, spoken introductions, visible clothing and frozen first impressions on retries")
    checked("selected native card as spoken dialogue, physical gestures, sensitive subjects, immutable replay and unspoken promises")


def check_resident_speech(api, model, say, path, common):
    cues={"Funny Joke":"Averil jokes about the tavern cook","Flirt":"Averil flirts nervously","Deep":"Averil asks about loss"}
    opening="The bread would keep a cartwheel turning."
    joke="The cook asked for a second opinion. The millstone chipped a tooth."
    promise="I will bring the cart tomorrow."
    for card,beat in (("Funny Joke",joke),("Flirt","*I smooth my sleeve.* I rather hoped you'd stay."),("Deep","")):
        turn=dict(say("Do you remember mending the wagon with me?"),returnCues=cues)
        choices={"Funny Joke":joke+" *Averil jokes about the tavern cook.*","Flirt":"*I smooth my sleeve.* I rather hoped you'd stay.",
                 "Deep":cues["Deep"]}
        sample=dict(common,warm=opening,neutral=opening,cool=promise,continuations=choices,
                    commitments=[{"speaker":"npc","quote":promise}])
        model.samples[turn["text"]]=json.dumps(sample)
        before=len(model.calls)
        ready=api("/api/dialogue/prepare",turn)
        expected=opening+(" "+beat if beat else "")
        assert ready["cardChoices"][card]["neutral"]==expected
        event=dict(kind="followup",action=card,cue=cues[card])
        api("/api/dialogue/finish",dict(turn,reaction="neutral",handoff=dict(event,cue="Changed variant")),status=409)
        done=api("/api/dialogue/finish",dict(turn,reaction="neutral",handoff=event))
        assert done["reply"]==expected and done["handoff"]==event
        assert api("/api/dialogue/prepare",turn)==done and api("/api/dialogue/finish",dict(turn,reaction="cool"))==done
        entry=next(item for item in api(path+"conversations")["items"] if item["id"]==turn["id"])
        assert entry["reply"]==expected and not entry.get("nativeHandoff") and not entry["promises"]
        assert len(model.calls)==before+1
    turn=dict(say("I'd like to sit quietly with his old fishing net."),returnCues=cues)
    line="*I sit beside you.* We can stay here a while."
    model.samples[turn["text"]]=json.dumps(dict(common,neutral=line,continuations={},commitments=[]))
    ready=api("/api/dialogue/prepare",turn)
    assert ready["cardChoices"]=={} and ready["choices"]["neutral"]==line
    end={"kind":"ended","cue":"Averil is done talking."}
    assert api("/api/dialogue/finish",dict(turn,reaction="neutral",handoff=end))["reply"]==line+" Be well."


def check_impressions(api, model, say, answer, coat):
    def impression(words, observation):
        turn = say(words)
        model.samples[words] = json.dumps(dict(answer, commitments=[]))
        assert api("/api/dialogue/prepare", dict(turn, playerImpression=observation))["state"] == "ready"
        prompt = model.calls[-1]["messages"][0]["content"]
        api("/api/dialogue/finish", dict(turn, reaction="neutral"))
        marker = "\nPlayer impression: "
        return json.loads(prompt.rsplit(marker, 1)[1]) if marker in prompt else None

    stranger = dict(coat, familiarity="Unmet", newlySeen=[])
    observed = {"relationshipToPlayer":"Unmet","visibleClothing":{"torso":"Festival Coat"},"newlySeen":[]}
    assert impression("*Alden bows.* What do you think of my coat?", stranger) == observed
    assert impression("Have you met Aldenford?", stranger) == observed
    introduced = impression("My name is Alden. Do you know it?", stranger)
    assert introduced == dict(observed, onlyIfIntroducingThemself={
        "name":"Alden","nameRecognition":"famous","publicStanding":"ill-regarded","publicTitle":"The Infamous"})
    little_known = impression("I'm Alden. Good evening.", dict(stranger, renown="little known"))
    assert little_known["onlyIfIntroducingThemself"] == {"name":"Alden","nameRecognition":"little known"}
    assert impression("What am I wearing today?", {"privateInventory":["Crown"],"visibleClothing":{
        "legs":["Silk"],"head":"x"*101,"ring":"Crown"},"newlySeen":["legs","head"]}) is None


def check_endings(api, model, say, path, ending):
    calls=len(model.calls)
    promise="I will bring a rake tomorrow."
    answer={
        "topic":"Leisure","warm":promise+" Would you like another story? *I nod.*",
        "neutral":"I do not know. Farewell.","cool":"*I shrug.* Would you ask again?",
        "farewells":{"warm":"Safe travels.","neutral":"I will bring a boat tomorrow.","cool":["Farewell."]},
        "commitments":[{"speaker":"npc","quote":promise},{"speaker":"npc","quote":"Safe travels."}]
    }
    cases=[
        ("warm",ending,promise+" *I nod.* Safe travels."),
        ("neutral",ending,"I do not know. Farewell."),
        ("cool",ending,"*I shrug.* Goodbye."),
        ("unknown",{"kind":"unknown"},"I do not know."),
    ]
    for reaction,decision,expected in cases:
        turn=say("What can you tell me about tending the garden?")
        model.samples[turn["text"]]=json.dumps(answer)
        prepared=api("/api/dialogue/prepare",turn)
        assert prepared["state"]=="ready" and prepared["choices"]["neutral"]=="I do not know."
        assert prepared["endingChoices"]["neutral"]=="I do not know. Farewell."
        result=api("/api/dialogue/finish",dict(turn,reaction=reaction,handoff=decision))
        assert result["reply"]==expected and result["handoff"]==decision
        conflicting=ending if decision["kind"]!="ended" else {"kind":"followup","action":"Leisure","cue":"The resident asks about fishing."}
        assert api("/api/dialogue/finish",dict(turn,reaction="neutral",handoff=conflicting))==api("/api/dialogue/prepare",turn)==result
        entry=next(item for item in api(path+"conversations")["items"] if item["id"]==turn["id"])
        assert entry["reply"]==expected
        assert [p["quote"] for p in entry["promises"]]==([promise] if reaction=="warm" else [])
    player_promise="I will mend the gate tomorrow."
    turn=say(player_promise+" Which poem do you like?")
    title='I like the poem “Who goes there?”'
    model.samples[turn["text"]]=json.dumps({"topic":"Leisure","warm":title,"neutral":"Good day.","cool":"I forget.",
        "commitments":[{"speaker":"player","quote":player_promise},{"speaker":"npc","quote":"Goodbye."}]})
    prepared=api("/api/dialogue/prepare",turn)
    assert prepared["choices"]["neutral"]=="Good day."
    assert api("/api/dialogue/finish",dict(turn,reaction="warm",handoff=ending))["reply"]==title+" Goodbye."
    entry=next(item for item in api(path+"conversations")["items"] if item["id"]==turn["id"])
    assert [(p["speaker"],p["quote"]) for p in entry["promises"]]==[("player",player_promise)]
    assert len(model.calls)==calls+len(cases)+1
