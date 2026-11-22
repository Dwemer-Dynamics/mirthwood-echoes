"""Check an ordinary copied-game Trade Ship visit offline: python tests/world_awareness_harbor.py RUN."""
import argparse
import json
import math
from pathlib import Path
import re
import sys


SHIP = "Trade Ship"
TRADER = re.compile(r"Trade Ship Merchant\(Clone\)/Dummy Trader(?: \(\d+\))?\Z")
BOARDING = re.compile(r"Load Stormreach/(?:Docks/(?:dockSouthShort|dockWestLong)|Trade Ship \([12]\)/Ship Structure/(?:shipBase \(1\)|ramp|platform))\Z")


def require(ok, message):
    if not ok:
        raise ValueError(message)


def lines(path):
    return [json.loads(line) for line in path.read_bytes().splitlines() if line.strip()]


def one(rows, label):
    require(len(rows) == 1, f"expected one {label}, found {len(rows)}")
    return rows[0]


def scope(row):
    return (row["resident"]["nativeId"], row["resident"]["owner"],
            *(row["world"][k] for k in ("slot", "created", "player", "memorySession", "day")))


def anchored(row, reference):
    return scope(row) == scope(reference)


def support(sample, prior_player=None, resident_plank=None, scenery=False, capsule=None):
    """Use the top solid contact in the recorded column, independent of the product floor query."""
    p = sample["point"]
    hits = sorted((h for h in sample["hits"] if h["position"][1] <= p[1] + .18),
                  key=lambda h: h["position"][1], reverse=True)
    if not hits:
        return None
    hit = hits[0]
    contact = hit["position"]
    if not (hit["type"] == "BoxCollider" and BOARDING.fullmatch(hit["path"]) and hit["normal"][1] >= .85
            and math.dist(p[::2], contact[::2]) < .01 and -.025 <= p[1] - contact[1] <= (3 if scenery else .35)):
        return None
    if any(other["collider"] != hit["collider"] and contact[1] - other["position"][1] <= .025 for other in hits[1:]):
        return None
    nav = sample.get("nav")
    if nav and math.dist(contact, nav) <= .18 and math.dist(contact[::2], nav[::2]) <= .06:
        return hit
    if not resident_plank or resident_plank["collider"] != hit["collider"]:
        return None
    if capsule is not None:
        clearance = capsule["bottom"] - contact[1]
        if capsule["enabled"] and capsule["detectCollisions"] and -.025 <= clearance <= min(.12, capsule["skinWidth"] + .02):
            return hit
        return None
    # Older recordings measured navigation on the last clear frame before Talk.
    if prior_player and math.dist(prior_player["point"], p) < .05 and p[1] - contact[1] <= .10:
        prior = support(prior_player)
        if prior and prior["collider"] == hit["collider"]:
            return hit
    return None


def ray(e, actor, subject):
    sight = e[subject]
    eye = [actor[0], actor[1] + .95, actor[2]]
    direction = [end - start for end, start in zip(sight["target"], eye)]
    facing = e["facing"]
    return (sight["clear"] is True and not sight["blockers"] and math.dist(eye, sight["target"]) <= 9 and
            math.dist(actor, sight["position"]) <= 9 and abs(actor[1] - sight["position"][1]) < 1.4 and
            facing[0] * direction[0] + facing[2] * direction[2] > 0)


def shared_level(resident, subject, first, second):
    if not first or not second or abs(first["position"][1] - second["position"][1]) > 1.4:
        return False
    if first["collider"] == second["collider"] or abs(first["position"][1] - second["position"][1]) <= .12:
        return True
    return not any(other["collider"] == end["collider"] and abs(other["position"][1] - start["position"][1]) > .12
                   for column, start, end in ((resident, first, second), (subject, second, first)) for other in column["hits"])


def local_view(rows, accepted, geometry, left_before_talk):
    venue = one([o for o in rows if o["stage"] == "at-venue"], "live venue")["extra"]
    path = venue["landmark"]
    require(venue["title"] == SHIP and venue["live"] and venue["root"] == "Trade Ship Merchant(Clone)" and
            TRADER.fullmatch(path) and venue["spawnedInstance"] and venue["landmarkInstance"], "missing native ship or trader instance")
    marks = one([o for o in rows if o["stage"] == "venue-landmarks"], "native landmarks")["extra"]
    require(any(m["path"] == path and math.dist(m["position"], venue["position"]) < .05 for m in marks), "trader missing from spawned landmarks")
    sight = one([o for o in rows if o["stage"] == "spectator-sightlines" and o["extra"]["phase"] == "before departure"], "final sightline")
    e = sight["extra"]
    require(anchored(sight, accepted) and sight["unityFrame"] <= accepted["unityFrame"] and
            e["root"] == venue["spawnedInstance"] and e["landmark"] == path and e["title"] == SHIP, "unrelated sightline")
    # A player can ask at the ship, or take the witness back to town before opening Talk.
    observation = sight if left_before_talk else accepted
    resident, player = observation["resident"], observation["player"]
    point = resident["position"]
    require(any(s["title"] == SHIP for s in observation["activeStories"]), "Trade Ship absent at the measured sighting")
    grounded = any(BOARDING.fullmatch(hit) for hit in resident["ground"])
    def measure(stage, frame):
        return one([g for g in geometry if g["stage"] == "geometry" and g["at"] == stage and
                    g["turn"] == accepted["turn"] and g["frame"] == frame], f"physical deck sample at {stage}")
    measurement = measure(observation["stage"], observation["unityFrame"])
    trader = one([t for t in measurement["traders"] if t["path"] == path and t["root"] == e["root"] and
                  t["id"] == venue["landmarkInstance"]], "same drawn trader")
    require(math.dist(point, measurement["resident"]["point"]) < .05 and
            math.dist(player["position"], measurement["player"]["point"]) < .05 and
            math.dist(venue["position"], trader["support"]["point"]) < .05, "deck receipt sampled other positions")
    drawn = trader["enabled"] and trader["active"] and not trader["forceRenderingOff"]
    resident_plank = support(measurement["resident"])
    trader_plank = support(trader["support"], scenery=True)
    capsule = measurement.get("playerCapsule")
    previous_player = None
    identified = player["name"] and player["name"] == sight["player"]["name"] and accepted["world"]["player"]
    if "playerRay" in measurement:
        eye = dict(facing=measurement["facing"], performer=trader["sight"], player=measurement["playerRay"])
        require(math.dist(eye["player"]["position"], player["position"]) < .01 and
                math.dist(eye["performer"]["target"], trader["boundsCenter"]) < .01, "eye ray sampled another target")
        identified = identified and (not capsule or
                     capsule["name"] == player["name"] and capsule["id"] == accepted["world"]["player"])
    else:
        require(math.dist(point, sight["resident"]["position"]) < .05 and
                math.dist(player["position"], sight["player"]["position"]) < .05 and
                math.dist(e["performer"]["target"], trader["boundsCenter"]) < .05,
                "positions changed after the last independent sightline")
        if not left_before_talk:
            previous_player = measure("spectator-sightlines", sight["unityFrame"])["player"]
        eye = e
    player_plank = support(measurement["player"], previous_player, resident_plank, capsule=capsule)
    trader_level = shared_level(measurement["resident"], trader["support"], resident_plank, trader_plank)
    player_level = shared_level(measurement["resident"], measurement["player"], resident_plank, player_plank)
    clear = bool(grounded and trader_level and drawn and e["isActiveAndEnabled"] and not e["IsDead"] and ray(eye, point, "performer"))
    company = bool(clear and identified and player_level and
                   ray(eye, point, "player") and math.dist(point, venue["position"]) <= 4 and
                   math.dist(player["position"], venue["position"]) <= 4)
    return clear, company, path, venue["position"], math.dist(point, venue["position"])


def ship_event(scene):
    events = [e for e in scene.get("today", []) if e["title"] == SHIP]
    return one(events, "Trade Ship fact") if events else None


def check_scene(scene, when, company, label):
    event = ship_event(scene)
    require(all(e.get("title") != SHIP for e in (scene.get("nextNotice", {}), *scene.get("nextByKind", {}).values())),
            f"{label} invented a posted Trade Ship date")
    if not when:
        require(not event, f"{label} granted a ship sighting without evidence")
        return
    require(event and event.get("source") == "firsthand" and event.get("status") == "active" and
            event.get("when") == when and event.get("seen") == "a trader", f"{label} missing firsthand trader ({when})")
    require(bool(event.get("playerInSight")) == (when == "in sight now" and company),
            f"{label} current player attendance differs from the independently measured shared view")


def literal(call):
    texts = (call["system"], call["user"])
    decoder = json.JSONDecoder()
    records = [decoder.raw_decode(t[m.end():])[0] for t in texts for m in re.finditer(r'"worldAwareness"\s*:\s*', t)]
    require(call["awareness"] in records, "provider receipt differs from literal model input")
    return call["awareness"]


def named_witness(call, player, day):
    recall = json.JSONDecoder().raw_decode(call["system"].split("Recall: ", 1)[1])[0]
    return any(w["day"] == day and SHIP in w["description"] and f"I saw {player} there." in w["description"]
               for w in recall.get("eventWitnesses", []))


def run(directory):
    read = lambda name: json.loads((directory / name).read_bytes())
    observations = lines(directory / "awareness-evidence/observations.jsonl")
    deck = directory / "awareness-evidence/quest-condition.jsonl"
    geometry = lines(deck)
    plan = read("world-awareness-plan.json")["turns"]
    calls = [json.loads(s.removeprefix("Echoes world awareness ")) for s in
             (directory / "server.log").read_text(encoding="utf-8", errors="replace").splitlines() if s.startswith("Echoes world awareness ")]
    journal = read("journal-observed.json") if (directory / "journal-observed.json").is_file() else []
    commits = read("turns.json") if (directory / "turns.json").is_file() else []
    sighting_places = {}
    named_attendance = set()
    failures = []
    require(any(t.get("venue", {}).get("title") == SHIP for t in plan), "run has no Trade Ship venue test")
    for index, step in enumerate(plan):
        rows = [o for o in observations if o["turn"] == index]
        try:
            source = one([o for o in rows if o["stage"] == "accepted"], "accepted Talk turn")
            intent = source["extra"]["intent"]
            key = source["extra"]["turn"]
            actor = source["resident"]
            stamp = scope(source)
            frozen = intent["worldAwareness"]
            require(intent["id"] == key == frozen["acceptedTurn"] and intent["text"] == step["words"] and
                    frozen["nativeId"] == actor["nativeId"] and frozen["owner"] == actor["owner"] and
                    frozen["world"] == source["world"] and frozen["day"] == source["world"]["day"], "accepted identity or branch mismatch")
            require(any(o["stage"] == "native-menu" and "Talk" in o["extra"]["labels"] and anchored(o, source)
                        for o in observations if o["unityFrame"] <= source["unityFrame"]), "missing native Talk menu")
            call = one([c for c in calls if c["turn"] == key], "ordinary provider call")
            require(call["mode"] == "private" and call["run"] == directory.name and call["nativeId"] == actor["nativeId"] and
                    call["day"] == source["world"]["day"], "provider dispatch belongs to another mode, run, resident or day")
            projected = literal(call)
            bubble = one([o["extra"] for o in rows if o["stage"] == "bubble" and o["extra"]["id"] == key], "displayed bubble")
            reply = bubble["reply"]
            require(reply and bubble["matched"] and bubble["visiblePages"], "no rendered reply")
            require(sum(t["player_text"] == intent["text"] and t["reply"] == reply and t["state"] == "complete" for t in commits) == 1 and
                    sum(j["id"] == key and j["player"] == intent["text"] and j["reply"] == reply for j in journal) == 1,
                    "displayed reply lacks a unique completed database turn or journal entry")
            print(f'Turn {index+1} · {actor["name"]} · {key} · {reply}')
            when, company = None, False
            left = step.get("returnFromVenue") == SHIP
            if left:
                require(any(o["stage"] == "left-venue" and o["extra"]["eventName"] == SHIP and
                            o["extra"]["distance"] > 9 and anchored(o, source) and o["unityFrame"] <= source["unityFrame"]
                            for o in rows), "missing independent departure")
            if step.get("venue", {}).get("title") == SHIP:
                current, company, path, spot, distance = local_view(rows, source, geometry, left)
                print(f"  {path} at {distance:.2f} m; clear trader={current}, independently visible player at venue={company}")
                view = step["venue"].get("view", "shared")
                expected = {"shared": (True, True), "event-only": (True, False), "blocked": (False, False)}
                require((current, company) == expected[view], f"the game never established the requested {view} view")
                if current:
                    sighting_places[stamp] = spot
                    if company:
                        named_attendance.add(stamp)
                    when = "earlier today" if left else "in sight now"
                elif stamp in sighting_places:
                    when = "earlier today"
            elif stamp in sighting_places:
                when = "earlier today"
            elif not left:
                require(not any(e["title"] == SHIP for e in source["activeStories"]), "baseline already has a live Trade Ship")
            if stamp in sighting_places and (left or not step.get("venue")):
                spot = sighting_places[stamp]
                require(math.dist(source["resident"]["position"], spot) > 9 and
                        math.dist(source["player"]["position"], spot) > 9, "remote Talk is still at the measured venue")
            check_scene(frozen, when, company, "accepted snapshot")
            check_scene(projected, when, company, "literal provider input")
            require(named_witness(call, source["player"]["name"], source["world"]["day"]) == (stamp in named_attendance),
                    "named attendance in recall differs from the observed company")
        except (ValueError, TypeError, KeyError, IndexError) as exc:
            failures.append(f"Turn {index + 1}: {exc}")
    require(not failures, "\n".join(failures))


def self_test():
    point = [0, 0, 0]
    e = {"facing": [1, 0, 0], "performer": {"position": [2, 0, 0], "target": [2, 1, 0], "clear": True, "blockers": []}}
    assert ray(e, point, "performer")
    e["performer"]["blockers"] = [{"collider": "wall"}]
    assert not ray(e, point, "performer")
    e["performer"].update(position=[2, 3, 0], blockers=[])
    assert not ray(e, point, "performer")
    plank = dict(path="Load Stormreach/Docks/dockSouthShort", type="BoxCollider", collider=1,
                 position=[986.15, -2.15, 113.23], normal=[0, 1, 0])
    feet = [986.15, -2.07, 113.23]
    measured = dict(point=feet, hits=[plank], nav=feet)
    edge = dict(measured, nav=[986.37, -2.09, 113.74])
    assert support(measured) == plank and support(edge) is None
    assert support(edge, measured, plank) == plank
    assert support(edge, measured, dict(plank, collider=2)) is None
    cap = dict(bottom=feet[1], skinWidth=.08, enabled=True, detectCollisions=True)
    assert support(edge, edge, plank, capsule=cap) == plank
    assert support(edge, edge, dict(plank, collider=2), capsule=cap) is None
    for changes in (dict(bottom=-1.85), dict(skinWidth=.01), dict(enabled=False), dict(detectCollisions=False)):
        assert support(edge, measured, plank, capsule=dict(cap, **changes)) is None
    assert support(dict(edge, point=[feet[0], -1.87, feet[2]]), measured, plank) is None
    assert support(dict(measured, hits=[dict(plank, path="roof")])) is None
    above = dict(plank, collider=2, position=[feet[0], -1.60, feet[2]])
    stacked = dict(measured, hits=[above, plank])
    assert not shared_level(stacked, measured, above, plank)
    assert shared_level(dict(measured, hits=[above]), measured, above, plank)
    check_scene({"today": []}, None, False, "unknown")
    old = dict(title=SHIP, source="firsthand", status="active", seen="a trader", when="earlier today", playerInSight=True)
    now = dict(old, when="in sight now")
    check_scene({"today": [now]}, "in sight now", True, "shared")
    check_scene({"today": [dict(now, playerInSight=False)]}, "in sight now", False, "trader alone")
    check_scene({"today": [dict(old, playerInSight=False)]}, "earlier today", False, "dated trader")
    for event, when, company in ((old, None, False), (old, "earlier today", False), (now, "in sight now", False),
                                 (dict(now, playerInSight=False), "in sight now", True)):
        try:
            check_scene({"today": [event]}, when, company, "unsupported attendance")
        except ValueError:
            continue
        raise AssertionError("unwitnessed current company passed")
    account = {"day": 58, "description": "I saw Trade Ship underway. I saw Michael there. I saw a trader."}
    call = {"system": "Recall: " + json.dumps({"eventWitnesses": [account]})}
    assert named_witness(call, "Michael", 58) and not named_witness(call, "Michael", 59)
    assert not named_witness(call, "Graham", 58)
    account["description"] = "I saw Trade Ship underway. I saw a trader."
    assert not named_witness({"system": "Recall: " + json.dumps({"eventWitnesses": [account]})}, "Michael", 58)
    print("Harbor footing, ray, upper-floor, shared, unknown and dated-company controls passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path, nargs="?")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if not (args.run or args.self_test):
        parser.error("supply a copied-game run or --self-test")
    try:
        if args.self_test:
            self_test()
        if args.run:
            run(args.run)
    except (OSError, ValueError, KeyError, TypeError, IndexError) as error:
        sys.exit(str(error))
