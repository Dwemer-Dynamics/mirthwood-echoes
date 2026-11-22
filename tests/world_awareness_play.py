"""Run player-style awareness checks against a private server and an isolated copy of slot 0."""
import argparse
import json
import os
from pathlib import Path
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

    def verify(self, token, work, probe):
        result = probe["result"]
        session = Path(os.environ["LOCALAPPDATA"]) / "MirthwoodEchoes/Harness" / uuid.UUID(hex=token).hex
        evidence = session / "awareness-evidence"
        observations = [json.loads(line) for line in (evidence/"observations.jsonl").read_text(encoding="utf-8").splitlines()]
        actual = result.get("turns", [])
        if not actual or not all(turn["matched"] and turn["visiblePages"] for turn in actual):
            raise AssertionError("Missing rendered player conversation.")
        prefix = "Echoes world awareness "
        trace = [json.loads(line[len(prefix):]) for line in (work/"server.log").read_text(encoding="utf-8", errors="replace").splitlines()
                 if line.startswith(prefix)]
        if any(item["run"] != token for item in trace):
            raise AssertionError("The provider trace belongs to another run.")
        game.write(work / "provider-inputs.json", trace)
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
                if item["actor"] != turn["actor"] or item["campaign"] != turn["campaign"] or item["model"] != model:
                    raise AssertionError("The actual call does not identify this speaker, campaign and model.")
                if item["user"] != turn["player"] or item["systemChars"] < 1 or item["userChars"] < 1 or not item["system"]:
                    raise AssertionError("Missing the submitted prompt or mismatched player words.")
        if not self.baseline and len([item for item in trace if item["mode"] == "private"]) != len(actual):
            raise AssertionError("More than one ordinary dialogue call per accepted player turn.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("case")
    parser.add_argument("--profile", type=Path, required=True, help="Public conversation options and selected model names.")
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("world_awareness_cases.json"))
    parser.add_argument("--baseline", action="store_true", help="Compare the pre-feature build with the same native observer.")
    args = parser.parse_args()
    case = load(args.cases)[args.case]
    if not case.get("resident") or not all(row.get("words") and row.get("check") for row in case["turns"]):
        parser.error("The case needs an identified resident and a question with an expected observation at every turn.")
    play = AwarenessPlay(case, load(args.profile), args.baseline)
    game.run(args.package.resolve(strict=True), "roleplay", case["resident"], case.get("startingDesire"), awareness=play)


if __name__ == "__main__":
    main()
