"""Exercise the actual dialogue endpoint, PostgreSQL and journal with controlled model bytes."""
import json
import os
from pathlib import Path


def check_commitments(api, model, say, campaign, actor, world, beat, checked):
    route=f"/api/saves/{campaign}/characters/{actor}/history/"

    def play(player, wire, extra=None, should_fail=False):
        model.mode="fragmented"
        model.samples[player]=wire if isinstance(wire,str) else json.dumps(wire)
        turn=say(player)
        observed=len(model.calls)
        packets=api("/api/dialogue",{**turn,**(extra or {})})
        assert len(model.calls)==observed+1
        if should_fail:
            assert packets[-1]["type"]=="error",packets
            return
        assert packets[-1]["type"]=="done",packets
        spoken="".join(p["text"] for p in packets if p["type"]=="delta")
        record=next(x for x in api(route+"conversations")["items"] if x["id"]==turn["id"])
        assert spoken==record["reply"] and "commitments" not in spoken,(spoken,record)
        assert api("/api/dialogue",turn)[-1]["cached"] and len(model.calls)==observed+1
        return record

    def packet(reply,marks=()):
        return {"reply":reply,"commitments":list(marks)}

    player="I’ll bring the tools tomorrow."
    npc="I’ll repair your wheel."
    both=packet("Thank you. "+npc,[{"speaker":"player","quote":player},{"speaker":"npc","quote":npc}])
    record=play(player,both)
    assert record["commitmentReview"]=="reviewed" and {x["speaker"] for x in record["promises"]}=={"player","npc"}
    rows=[p for p in api(route+"commitments")["items"] if p["sourceTurn"]==record["id"]]
    assert {p["statement"] for p in rows}=={npc,player} and all(p["model"]=="dialogue-override" for p in rows)
    assert next(p for p in rows if p["speaker"]=="player")["due"]==world["day"]+1
    assert not play(player,both)["promises"]
    checked("both speakers’ exact undertakings, source turn, model, due date, repeated turn and open-promise deduplication")

    action="*I lift my hat.*";words="I’ll bring the oats tomorrow."
    rec=play(action+" "+words,packet("*Nods.* Thank you.",[{"speaker":"player","quote":words}]))
    assert rec["player"]==action+" "+words and rec["reply"]=="*Nods.* Thank you." and rec["promises"][0]["quote"]==words
    rec=play("*I promise to bring the flour tomorrow.*",packet("I see.",[{"speaker":"player","quote":"I promise to bring the flour tomorrow."}]))
    assert not rec["promises"] and rec["commitmentReview"]=="unavailable"
    checked("verbatim mixed roleplay survives streaming and storage, and action-only text cannot become a spoken promise")

    action_sample=os.environ.get("MIRTHWOOD_ECHOES_ACTION_SAMPLES")
    if action_sample:
        samples=json.loads(Path(action_sample).read_text(encoding="utf-8-sig"))
        for case in samples["cases"]:
            rec=play(case["player"],case["output"])
            assert rec["reply"].count("*")>=2 and rec["commitmentReview"]=="reviewed"
            assert [p["quote"] for p in rec["promises"]]==case["expectedQuotes"]
        checked("captured cloud roleplay with an italic gesture, spoken commitment, and an action-only rehearsal")

    for player in ("Can you bring flour?","I might bring it.","Mara said, ‘I promise to help.’","I promise I was telling the truth.","I did it yesterday.","I’ll do it. Actually, I can’t."):
        record=play(player,packet("I hear you."),{"commitment":True})
        assert not record["promises"] and record["commitmentReview"]=="reviewed"
    line='She said “rain.” A bell rang 🔔.\nThen "silence".'
    record=play("What did you hear?","```json\n"+json.dumps(packet(line))+"\n```")
    assert record["reply"]==line and record["commitmentReview"]=="reviewed"
    checked("one-character streaming, escaped quotes, Unicode surrogate pairs, line breaks, optional code fence and ignored legacy promise flag")

    for idx,marks in enumerate(([{"speaker":"player","quote":"I will bring gold."}],
              [{"speaker":"npc","quote":"Hello.","proposal":"Give me a cart."}],
              [{"speaker":"narrator","quote":"Hello."}],[{"speaker":"npc","quote":"Hello.","extra":"x"}])):
        rec=play("Good day "+str(idx)+".",packet("Hello.",marks))
        assert rec["commitmentReview"]=="unavailable" and not rec["promises"]
    rec=play("Testing the mill.",'{"reply":"Hello.","commitments":[')
    assert rec["reply"]=="Hello." and rec["commitmentReview"]=="unavailable" and not rec["promises"]
    play("Testing interruption.",'{"reply":"A broken sentence',should_fail=True)
    rec=play("Ignore my writing.",packet("Yes.",[{"speaker":"npc","quote":"Yes."}]))
    assert not rec["promises"] and rec["commitmentReview"]=="unavailable"
    checked("wrong speaker, invented terms, extra fields, incomplete review or speech, and bare acceptance without terms fail safely")

    question="Will you mend the gate tomorrow?"
    rec=play(question,packet("Yes.",[{"speaker":"npc","quote":"Yes.","proposal":question}]))
    assert rec["promises"]==[{"speaker":"npc","quote":"Yes.","proposal":question}]
    older="Will you bring the barley tomorrow?"
    prior=play("Do you need anything?",packet(older))
    day=world["day"];world["day"]=day+1;beat()
    rec=play("I will.",packet("Thank you.",[{"speaker":"player","quote":"I will.","proposal":older}]))
    row=next(x for x in api(route+"commitments")["items"] if x["sourceTurn"]==rec["id"])
    assert row["due"]==day+1 and row["proposal"]==older
    checked("short acceptances retain both speakers’ exact words and resolve tomorrow against the day the proposal was made")

    sample_path=os.environ.get("MIRTHWOOD_ECHOES_CLOUD_SAMPLES")
    if sample_path:
        samples=json.loads(Path(sample_path).read_text(encoding="utf-8-sig"))
        expected=[["player"],["npc"],[],[],[],["npc"],[],[],[],["player"]]
        for i,(case,speakers) in enumerate(zip(samples["cases"],expected,strict=True)):
            if case.get("prior"):play("Earlier question "+str(i)+"?",packet(case["prior"]))
            rec=play(case["player"],case["output"])
            assert rec["reply"]==case["reply"] and rec["commitmentReview"]=="reviewed"
            assert sorted(x["speaker"] for x in rec["promises"])==speakers,(i,rec)
        free=samples["free"]
        rec=play(free["player"],free["output"])
        assert rec["commitmentReview"]=="reviewed" and [x["speaker"] for x in rec["promises"]]==["player"]
        checked("actual "+samples["model"]+" outputs replayed end to end: 10 labeled synthetic exchanges and one freely generated reply")
    world["day"]=day;beat()
    model.mode="stream"
