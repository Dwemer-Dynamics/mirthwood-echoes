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


def support(sample, prior_player=None, resident_plank=None, scenery=False):
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
    # The player stayed on this physical plank while the Talk actors displaced the nav sample.
    if prior_player and resident_plank and math.dist(prior_player["point"], p) < .05 and p[1] - contact[1] <= .10:
        prior = support(prior_player)
        if prior and prior["collider"] == resident_plank["collider"] == hit["collider"]:
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


def local_view(rows, accepted, geometry):
    venue = one([o for o in rows if o["stage"] == "at-venue"], "live venue")["extra"]
    path = venue["landmark"]
    require(venue["title"] == SHIP and venue["live"] and venue["root"] == "Trade Ship Merchant(Clone)" and
            TRADER.fullmatch(path) and venue["spawnedInstance"] and venue["landmarkInstance"], "missing native ship or trader instance")
    marks = one([o for o in rows if o["stage"] == "venue-landmarks"], "native landmarks")["extra"]
    require(any(m["path"] == path and math.dist(m["position"], venue["position"]) < .05 for m in marks), "trader missing from spawned landmarks")
    sight = one([o for o in rows if o["stage"] == "spectator-sightlines" and o["extra"]["phase"] == "before departure"], "final sightline")
    e = sight["extra"]
    resident, player = accepted["resident"], accepted["player"]
    require(anchored(sight, accepted) and sight["unityFrame"] <= accepted["unityFrame"] and
            e["root"] == venue["spawnedInstance"] and e["landmark"] == path and e["title"] == SHIP, "unrelated sightline")
    point = resident["position"]
    require(math.dist(point, sight["resident"]["position"]) < .05 and math.dist(player["position"], sight["player"]["position"]) < .05,
            "positions changed after the last independent sightline; current sight is unverified")
    require(any(s["title"] == SHIP for s in accepted["activeStories"]), "Trade Ship absent from native active stories at Talk")
    grounded = e["solidFloor"] and any(BOARDING.fullmatch(hit) for hit in resident["ground"])
    def measure(stage, frame):
        return one([g for g in geometry if g["stage"] == "geometry" and g["at"] == stage and
                    g["turn"] == accepted["turn"] and g["frame"] == frame], f"physical deck sample at {stage}")
    measurement = measure("accepted", accepted["unityFrame"])
    previous = measure("spectator-sightlines", sight["unityFrame"])
    trader = one([t for t in measurement["traders"] if t["path"] == path and t["root"] == e["root"] and
                  t["id"] == venue["landmarkInstance"]], "same drawn trader")
    require(math.dist(point, measurement["resident"]["point"]) < .05 and
            math.dist(player["position"], measurement["player"]["point"]) < .05 and
            math.dist(venue["position"], trader["support"]["point"]) < .05 and
            math.dist(e["performer"]["target"], trader["boundsCenter"]) < .05, "deck or eye ray sampled other positions")
    drawn = trader["enabled"] and trader["active"] and not trader["forceRenderingOff"]
    resident_plank = support(measurement["resident"])
    trader_plank = support(trader["support"], scenery=True)
    player_plank = support(measurement["player"], previous["player"], resident_plank)
    trader_level = shared_level(measurement["resident"], trader["support"], resident_plank, trader_plank)
    player_level = shared_level(measurement["resident"], measurement["player"], resident_plank, player_plank)
    clear = bool(grounded and trader_level and drawn and e["isActiveAndEnabled"] and not e["IsDead"] and ray(e, point, "performer"))
    identified = player["name"] and player["name"] == sight["player"]["name"] and accepted["world"]["player"]
    company = bool(clear and identified and player_level and resident["layer"] == player["layer"] and
                   ray(e, point, "player") and math.dist(point, venue["position"]) <= 4 and
                   math.dist(player["position"], venue["position"]) <= 4)
    return clear, company, path, math.dist(point, venue["position"])


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
    seen = set()
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
            prior = stamp in seen
            when, company = None, False
            if step.get("venue", {}).get("title") == SHIP:
                current, company, path, distance = local_view(rows, source, geometry)
                print(f"  {path} at {distance:.2f} m; clear trader={current}, independently visible player at venue={company}")
                if current:
                    when = "in sight now"
                    seen.add(stamp)
                elif prior:
                    when = "earlier today"
            elif step.get("returnFromVenue") == SHIP:
                require(any(o["stage"] == "left-venue" and o["extra"]["eventName"] == SHIP and
                            o["extra"]["distance"] > 9 and anchored(o, source) for o in rows), "missing independent departure")
                when = "earlier today" if prior else None
            else:
                require(not any(e["title"] == SHIP for e in source["activeStories"]), "baseline already has a live Trade Ship")
            check_scene(frozen, when, company, "accepted snapshot")
            check_scene(projected, when, company, "literal provider input")
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
