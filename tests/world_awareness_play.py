"""Run player-style awareness checks against a private server and an isolated copy of slot 0."""
import argparse
import json
import os
from pathlib import Path
import re
import time
import uuid

import resident_dialogue as game


def load(path):
    return json.loads(path.read_bytes())


class AwarenessPlay:
    def __init__(self, plan, profile, baseline):
        self.plan = plan
        self.profile = profile
        self.baseline = baseline

    def configure(self, token, work):
        if game.api("test-session")["run"] != token or work.name != token:
            raise ValueError("The provider must belong to this private session.")
        options = self.profile["options"]
        if options["provider"] != "codex_subscription":
            raise ValueError("This driver expects the selected Windows subscription profile.")
        provider = "providers/" + options["provider"]
        selected = self.profile[provider]
        allowed = {"defaultModel", "conversation", "memory", "conversationEffort", "codexHost"}
        if set(selected) != allowed or selected["codexHost"] != "windows":
            raise ValueError("Expected public model choices from the Windows subscription.")
        for path, values in ((provider, selected), ("options", options)):
            game.api(path, values, "PUT")
            actual = game.api(path)
            if any(actual.get(key) != value for key, value in values.items()):
                raise RuntimeError("The private backend has different choices for " + path)
        game.write(work / "configured-profile.json", self.profile)

    def words(self, index):
        return self.plan["turns"][index]["words"]

    def observe(self, work, session):
        pending = session / "address-retry.json"
        ready = session / "address-dispatched"
        if not pending.is_file() or ready.exists():
            return
        request = load(pending)
        for line in (work / "server.log").read_text(encoding="utf-8",errors="replace").splitlines(keepends=True):
            prefix = "Echoes world awareness "
            if line.endswith("\n") and line.startswith(prefix):
                call = json.loads(line[len(prefix):])
                if call["turn"] == request["id"] and call["mode"] == "address" and call["run"] == work.name:
                    tmp = ready.with_suffix(".tmp")
                    tmp.write_text(request["id"],encoding="utf-8")
                    tmp.replace(ready)
                    return

    def verify_call(self, item, observed):
        selected = self.profile["providers/codex_subscription"]
        if item["provider"] != self.profile["options"]["provider"] or item["model"] != (selected["conversation"] or selected["defaultModel"]):
            raise AssertionError("The dispatch used different model settings.")
        if item["nativeId"] != observed["resident"]["nativeId"] or item["day"] != observed["world"]["day"]:
            raise AssertionError("The provider received another resident or game day.")
        for field in ("system", "user"):
            if not item[field] or len(item[field].encode("utf-16-le")) // 2 != item[field + "Chars"]:
                raise AssertionError("The provider input character count does not match the captured text.")
        context = item["awareness"]
        if not context:
            raise AssertionError("The supported game scene lost its world context.")
        actual = []
        for field in ("system", "user"):
            for match in re.finditer(r'"worldAwareness"\s*:\s*', item[field]):
                actual.append(json.JSONDecoder().raw_decode(item[field][match.end():])[0])
        if context not in actual:
            raise AssertionError("The world context receipt differs from the literal provider input.")
        clock = observed["calendar"]
        date = context["calendar"]
        # The game HUD has no year-zero label. The model can omit the native zero-based year.
        if (date["season"], date["dayOfSeason"], date["timeOfDay"], date.get("year", 0)) != (
                clock["hudSeason"], int(clock["hudDay"]), clock["TimeOfDayString"], clock["YearNumber"]):
            raise AssertionError("The speaker's date or time differs from the native HUD and clock.")
        if observed["resident"].get("homeTown") == "Brightoak":
            # All seven entries were transcribed from the two opened native notices in this game build.
            posted = {
                ("Spring", 14): "Grand Joust", ("Spring", 21): "Festival of Blossoms",
                ("Summer", 10): "Grand Joust", ("Summer", 21): "Summer Solstice",
                ("Fall", 12): "Grand Joust", ("Fall", 17): "All Hallows Eve", ("Winter", 10): "Yuletide",
            }
            due = posted.get((date["season"], date["dayOfSeason"]))
            if due and due not in [e["title"] for e in context.get("today", [])]:
                raise AssertionError("The local speaker's prompt omits today's public " + due)

    def verify(self, token, work, probe):
        result = probe["result"]
        session = Path(os.environ["LOCALAPPDATA"]) / "MirthwoodEchoes/Harness" / uuid.UUID(hex=token).hex
        evidence = session / "awareness-evidence"
        observations = [json.loads(line) for line in (evidence/"observations.jsonl").read_text(encoding="utf-8").splitlines()]
        prefix = "Echoes world awareness "
        trace = [json.loads(line[len(prefix):]) for line in (work/"server.log").read_text(encoding="utf-8", errors="replace").splitlines()
                 if line.startswith(prefix)]
        if any(item["run"] != token for item in trace):
            raise AssertionError("The provider trace belongs to another run.")
        game.write(work / "provider-inputs.json", trace)
        if self.plan.get("readPosters"):
            read = {o["extra"]["option"] for o in observations if o["stage"] == "read-poster" and o["extra"]["labels"]}
            if read != {"Events", "Jousting"}:
                raise AssertionError("Both physical poster windows must be opened and readable.")
        route = self.plan.get("mode", "roleplay")
        if route == "grouptalk":
            self.verify_group(work, result, observations, trace)
            return
        if self.plan.get("questQuestion"):
            self.verify_quest(work, observations, trace)
        actual = result.get("turns", [])
        if not actual or not all(turn["matched"] and turn["visiblePages"] for turn in actual):
            raise AssertionError("Missing rendered player conversation.")
        first = [item for item in observations if item["stage"] == "before-approach"]
        if len(first) != 1 or first[0]["resident"]["name"] != result["resident"]:
            raise AssertionError("Missing original resident and scene.")
        records = []
        for index, turn in enumerate(actual):
            step = self.plan["turns"][index]
            if turn["player"] != step["words"]:
                raise AssertionError("The in-game player draft differs from the planned question.")
            accepted, = [item for item in observations if item["stage"] == "accepted" and item["extra"]["turn"] == turn["id"]]
            bubble, = [item for item in observations if item["stage"] == "bubble" and item["extra"]["id"] == turn["id"]]
            if bubble["extra"]["reply"] != turn["reply"]:
                raise AssertionError("The captured bubble differs from the stored reply.")
            matched = [item for item in trace if item["turn"] == turn["id"] and item["mode"] == "private"]
            record = {"turn":turn["id"], "speaker":result["resident"], "player":turn["player"], "reply":turn["reply"],
                      "expected":step["check"], "firstReadableMs":turn["firstMs"], "finishedMs":turn["finishMs"],
                      "source":accepted, "bubble":bubble, "provider":matched}
            records.append(record)
        game.write(work / "world-awareness-turns.json", records)
        expected = {row["id"]: row["reply"] for row in actual}
        first_turn = actual[0]
        history = f"saves/{first_turn['campaign']}/characters/{first_turn['actor']}/history/conversations"
        # Older days may already have compacted. Compare every full entry still on the current day.
        deadline = time.monotonic() + 10
        while True:
            entries = [row for row in game.api(history)["items"] if row["id"] in expected]
            if any(row["id"] == actual[-1]["id"] for row in entries) or time.monotonic() >= deadline:
                break
            time.sleep(.3)
        game.write(work / "journal-observed.json", entries)
        if not any(row["id"] == actual[-1]["id"] for row in entries) or any(row["reply"] != expected[row["id"]] for row in entries):
            raise AssertionError("The current conversation in the private journal differs from the displayed words.")
        if len(actual) != len(self.plan["turns"]):
            raise AssertionError(f"The native visit ended after {len(actual)} of {len(self.plan['turns'])} planned turns.")
        model = self.profile["providers/codex_subscription"]["conversation"] or self.profile["providers/codex_subscription"]["defaultModel"]
        for turn,record in zip(actual,records):
            if not self.baseline:
                item, = record["provider"]
                self.verify_call(item,record["source"])
                intent = record["source"]["extra"]["intent"]
                stored = intent.get("worldAwareness")
                if not stored or stored.get("acceptedTurn") != turn["id"] or "worldAwareness" in (intent.get("context") or {}):
                    raise AssertionError("The accepted snapshot is missing its turn identity or entered the immutable cue binding.")
                if item["actor"] != turn["actor"] or item["campaign"] != turn["campaign"] or item["model"] != model:
                    raise AssertionError("The actual call does not identify this speaker, campaign and model.")
                if item["user"] != turn["player"] or item["systemChars"] < 1 or item["userChars"] < 1 or not item["system"]:
                    raise AssertionError("Missing the submitted prompt or mismatched player words.")
        if not self.baseline and len([item for item in trace if item["mode"] == "private"]) != len(actual):
            raise AssertionError("More than one ordinary dialogue call per accepted player turn.")
        if self.plan.get("compareFallPosters") and not self.baseline:
            self.verify_fall_posters(records, observations)
        if self.plan["turns"][0].get("watchResidentWalk") and not self.baseline:
            walking, = [o for o in observations if o["stage"] == "walking-before-talk"]
            active = {task["name"] for task in walking["resident"]["jobs"]
                      if task["sameOwner"] and task["enabled"] and task["status"] == "Continue"}
            if "Go To Work" not in active or walking["resident"]["stopped"] or walking["extra"]["moved"] <= .4:
                raise AssertionError("The copied visit did not establish the resident's own walk to work.")
            for record in records[:2]:
                activity = record["provider"][0]["awareness"].get("interruptedActivity", "")
                if "work" not in activity.casefold():
                    raise AssertionError("The accepted provider input lost the resident's observed trip to work.")
        if not self.baseline:
            for step, row in zip(self.plan["turns"], records):
                if "venue" not in step:
                    continue
                venue = step["venue"]
                status = "ended" if venue.get("end") else "active"
                today = row["provider"][0]["awareness"].get("today", [])
                if not any(e["title"] == venue["title"] and e["status"] == status and e["source"] == "firsthand" for e in today):
                    raise AssertionError("The speaker at the confirmed venue lacks the witnessed " + status + " event.")
        if route == "residentdialogue":
            addressed = []
            for opener in result["openers"]:
                bubble, = [o for o in observations if o["stage"] == "address-bubble" and o["extra"]["id"] == opener["id"]]
                before, = [o for o in observations if o["stage"] == "address-before" and o["turn"] == bubble["turn"]]
                if not opener["matched"] or not bubble["extra"]["rendered"] or not result["backgroundNative"]:
                    raise AssertionError("Missing a rendered player-directed opener or the unchanged background interaction.")
                calls = [t for t in trace if t["mode"] == "address" and t["turn"] == opener["id"]]
                if calls:
                    self.verify_call(calls[0],before)
                addressed.append(dict(opener=opener, before=before, bubble=bubble, provider=calls))
            game.write(work / "world-awareness-openers.json", addressed)
            if not self.baseline and (len(addressed) != 2 or any(len(row["provider"]) != 1 for row in addressed)):
                raise AssertionError("A native opener did not have one corresponding provider call.")
            if self.plan.get("retryAddress"):
                interrupted, = [o for o in observations if o["stage"] == "address-disconnect"]
                if interrupted["extra"]["id"] != addressed[0]["opener"]["id"]:
                    raise AssertionError("The recovered native opener has a different identity.")
            expected = {game.opener_journal_id(row["opener"]): row["opener"]["line"] for row in addressed}
            written = [row for row in game.api(history)["items"] if row["id"] in expected]
            game.write(work / "journal-openers.json", written)
            if {row["id"]: row["reply"] for row in written} != expected or len(written) != len(expected):
                raise AssertionError("The passing remark recovery disagrees with the private journal.")

    def verify_fall_posters(self, records, observations):
        visits = {row["source"]["extra"]["intent"]["context"]["conversation"] for row in records}
        if len(visits) != 1:
            raise AssertionError("The festival follow-up must belong to the original conversation.")
        boards = {o["extra"]["option"]: o["extra"]["labels"] for o in observations if o["stage"] == "read-poster"}
        for index, (board, kind, title, label, day, spoken_day) in enumerate((
            ("Jousting", "joust", "Grand Joust", "12th of Fall", 12, r"\b(?:12(?:th)?|twelfth)\b"),
            ("Events", "festival", "All Hallows Eve", "17th of Fall", 17, r"\b(?:17(?:th)?|seventeenth)\b"),
        )):
            row = records[index]
            info = row["provider"][0]["awareness"]
            notice = info.get("nextByKind", {}).get(kind, {})
            if title not in boards[board] or label not in boards[board] or (
                notice.get("title"), notice.get("season"), notice.get("dayOfSeason"), notice.get("source")
            ) != (title, "Fall", day, "public notice"):
                raise AssertionError("The category-specific provider input differs from the opened " + board + " poster.")
            words = row["reply"].casefold().replace("’", "").replace("'", "")
            if not re.search(spoken_day, words) or (kind == "festival" and "all hallows" not in words):
                raise AssertionError("Review the displayed words for the advertised " + title + " date.")

    def verify_group(self, work, result, observations, trace):
        guests = {g["actor"]: g["name"] for g in result["guests"]}
        lines = result["groupLines"]
        attendance = result["attendance"]
        result_rows = []
        for entry in lines:
            line = entry["line"]
            actor = line["speaker"]
            present = attendance["joinedAfter"] < line["seq"] <= attendance["departedAfter"]
            if (attendance["late"] in line["audience"]) != present:
                raise AssertionError("A late arrival heard a line outside their visit.")
            if actor == "player":
                continue
            bubble, = [o for o in observations if o["stage"] == "group-bubble" and o["extra"]["id"] == entry["id"]]
            before, = [o for o in observations if o["stage"] == "group-dispatch" and o["extra"]["revision"] == bubble["extra"]["revision"]]
            if bubble["resident"]["name"] != guests[actor] or before["resident"]["name"] != guests[actor] or not bubble["extra"]["ink"]:
                raise AssertionError("The sampled speaker and the rendered group line disagree.")
            result_rows.append(dict(entry=entry, before=before, bubble=bubble))
        calls = [t for t in trace if t["mode"] == "group"]
        game.write(work / "world-awareness-group.json", dict(lines=result_rows, calls=calls, attendance=attendance))
        for actor, name in guests.items():
            path = f"saves/{result['campaign']}/characters/{actor}/history/sessions/{result['groupSession']}"
            journal = game.api(path)
            game.write(work / (name + "-heard.json"), journal)
            expected = [r["line"]["words"] for r in lines if actor in r["line"]["audience"]]
            if [row["text"] for row in journal] != expected:
                raise AssertionError("The journal differs from what " + name + " heard.")
        aside, = [o for o in observations if o["stage"] == "private-aside-bubble"]
        if not aside["extra"]["bubble"] or aside["extra"]["text"] != self.words(0):
            raise AssertionError("The private setup was never spoken.")
        if not self.baseline:
            if [t["actor"] for t in calls] != [r["entry"]["line"]["speaker"] for r in result_rows]:
                raise AssertionError("Group dispatches and actual speakers differ.")
            by_id = {entry["id"]: entry["line"] for entry in lines}
            for t, row in zip(calls, result_rows):
                self.verify_call(t,row["before"])
                if t["turn"] != row["entry"]["id"]:
                    raise AssertionError("The provider request belongs to a different native group line.")
                # The fixed writing example already contains "2 Flour". Only this exact
                # occurrence is independent of the private collection made in this visit.
                example = 'For an inventory count such as 2 Flour, say "some flour"'
                prompt = (t["system"].replace(example, "", 1) + "\n" + t["user"]).casefold()
                spoken = row["entry"]["line"]["words"].casefold()
                if any(term.casefold() in prompt or term.casefold() in spoken for term in self.plan["privateTerms"]):
                    raise AssertionError("Private information reached a group speaker or the circle.")
                shared = json.loads(t["user"])
                for prior in shared["heardInOrder"]:
                    original = by_id[prior["id"]]
                    if not set(row["entry"]["line"]["audience"]).issubset(original["audience"]) or original["words"] != prior["words"]:
                        raise AssertionError("A group prompt reveals a line outside the shared audience.")
                if t["campaign"] != result["campaign"] or t["nativeId"] != row["before"]["resident"]["nativeId"]:
                    raise AssertionError("Group context belongs to another resident or campaign.")
        if self.plan.get("hearingQuestion"):
            heard = [o for o in observations if o["stage"] == "hearing-followup-bubble"]
            if [o["resident"]["name"] for o in heard] != ["Drystan", "Anastasia"]:
                raise AssertionError("Missing the original listener or the resident outside the group.")
            for observation in heard:
                data = observation["extra"]
                row = data["row"]
                present = any(row["actor"] in entry["line"]["audience"] for entry in lines)
                if present != (observation["resident"]["name"] == "Drystan") or not data["matched"]:
                    raise AssertionError("The later private question lost its original hearing boundary.")
                history = f"saves/{row['campaign']}/characters/{row['actor']}/history/conversations"
                saved, = [item for item in game.api(history)["items"] if item["id"] == row["id"]]
                if saved["reply"] != data["reply"]:
                    raise AssertionError("The private follow-up differs from the delivered and journaled answer.")
                if not self.baseline:
                    call, = [t for t in trace if t["mode"] == "private" and t["turn"] == row["id"]]
                    if call["actor"] != row["actor"] or (not present and "copper streamer" in (call["system"] + data["reply"]).casefold()):
                        raise AssertionError("A private listener received the wrong actor's group account.")

    def verify_quest(self, work, observations, trace):
        before, = [o for o in observations if o["stage"] == "quest-before"]
        offer, = [o for o in observations if o["stage"] == "quest-offer"]
        portrait, = [o for o in observations if o["stage"] == "quest-portrait"]
        intention, = [o for o in observations if o["stage"] == "quest-attendance-intention"]
        outcome, = [o for o in observations if o["stage"] == "quest-outcome"]
        if self.plan.get("questDecision") and outcome["extra"]["request"]["text"] != self.plan["questDecision"]:
            raise AssertionError("The native decision used different words from the player's mixed-subject response.")
        question = portrait["extra"]
        decline = self.plan.get("declineQuest", False)
        if before["extra"]["terms"] != offer["extra"]["nativeTerms"] or question["terms"] != outcome["extra"]["terms"] or outcome["extra"]["count"] != int(not decline):
            raise AssertionError("The incidental world question changed the native offer.")
        if question["request"]["text"] != self.plan["questQuestion"] or question["result"]["action"] != "continue" or not question["rendered"]:
            raise AssertionError("Missing the incidental answer in the actual quest portrait.")
        calls = [t for t in trace if t["mode"] == "quest" and t["turn"] == question["request"]["id"]]
        game.write(work / "world-awareness-quest.json", dict(before=before, offer=offer, portrait=portrait, intention=intention, provider=calls, outcome=outcome))
        if intention["extra"]["journal"] or not intention["extra"]["originalOffer"] or intention["extra"]["result"]["action"] != "continue":
            raise AssertionError("The player's intention to attend a joust changed the quest.")
        steps = [o["extra"]["request"]["id"] for o in (offer,portrait,intention,outcome)]
        if not self.baseline and [t["turn"] for t in trace if t["mode"] == "quest"] != steps:
            raise AssertionError("The quest calls differ from the four accepted native steps.")
        if not self.baseline and (len(calls) != 1 or calls[0]["actor"] != question["request"]["actor"]):
            raise AssertionError("The quest question lacks a single call for the offering resident.")
        if calls:
            self.verify_call(calls[0],before)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("case")
    parser.add_argument("--profile", type=Path, required=True, help="Public conversation options and selected model names.")
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("world_awareness_cases.json"))
    parser.add_argument("--baseline", action="store_true", help="Compare the pre-feature build with the same native observer.")
    parser.add_argument("--installed", action="store_true", help="Smoke test a matching installed release with Steam's normal loader.")
    args = parser.parse_args()
    case = load(args.cases)[args.case]
    if not case.get("resident") or not all(row.get("words") and row.get("check") for row in case["turns"]):
        parser.error("The case needs an identified resident and a question with an expected observation at every turn.")
    play = AwarenessPlay(case, load(args.profile), args.baseline)
    mode = case.get("mode", "roleplay")
    resident = case["resident"] if mode in ("roleplay", "grouptalk") else None
    game.run(args.package.resolve(strict=True), mode, resident, case.get("startingDesire"), awareness=play, installed=args.installed)


if __name__ == "__main__":
    main()
