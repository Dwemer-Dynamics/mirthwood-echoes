"""Check and package the original voice recordings for a server release."""
import argparse
from array import array
from collections import Counter
import hashlib
import io
import json
import math
from pathlib import Path
import wave
import zipfile


def main():
    args = argparse.ArgumentParser()
    args.add_argument("masters", type=Path, help="Directory with cast/<voice> WAVs and narrator.wav")
    args.add_argument("--report", type=Path)
    options = args.parse_args()
    voices = Path(__file__).resolve().parents[1] / "voices"
    cast = json.loads((voices / "cast.json").read_text(encoding="utf-8"))["voices"]
    cues = json.loads((voices / "cues.json").read_text(encoding="utf-8"))
    reactions = set(cues["events"].values())
    if len(cast) != 32 or len({v["id"] for v in cast}) != 32 or len(reactions) != 33 or len(cues["events"]) != 36:
        raise ValueError("The first-round cast or native reaction index is incomplete.")
    expected = {(accent, sex): (6 if accent == "American" else 2)
                for accent in ("American", "British", "Spanish", "German", "Russian", "Chinese")
                for sex in ("Male", "Female")}
    if reactions != set(cues["takes"]) or Counter((v["accent"], v["sex"]) for v in cast) != expected:
        raise ValueError("The reaction or accent roster differs from the first-round cast.")
    samples = {}
    fingerprints = set()
    audited = {}
    audit_path = options.masters / "lexical" / "audit.json"
    if audit_path.is_file():
        audits = json.loads(audit_path.read_text(encoding="utf-8"))
        spoken = {"greet", "anxious", "encourage", "flirt", "inspire", "thinking", "intimidate", "scare"}
        for member in cast:
            actor = member["id"]
            result = audits.get(actor, {})
            if result.get("missing") or set(result.get("matched", {})) != spoken:
                raise ValueError(f"The spoken reaction check is still incomplete for {actor}.")
            for cue, take in result["matched"].items():
                audited[f"voices/{actor}/{cue}.wav"] = take["sha256"]

    def read(name, path):
        data = path.read_bytes()
        if name in audited and hashlib.sha256(data).hexdigest() != audited[name]:
            raise ValueError(f"The recording changed after its spoken reaction check: {name}")
        with wave.open(io.BytesIO(data)) as source:
            if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getcomptype()) != (1, 2, 24000, "NONE"):
                raise ValueError(f"Unexpected WAV format in {path}")
            pcm = array("h")
            pcm.frombytes(source.readframes(source.getnframes()))
        digest = hashlib.sha256(pcm.tobytes()).hexdigest()
        duration = len(pcm) / 24000
        rms = math.sqrt(sum(v*v for v in pcm) / max(1, len(pcm))) / 32768
        if digest in fingerprints or duration < 0.07 or rms < 0.001 or max(map(abs, pcm), default=0) < 180:
            raise ValueError(f"Empty, inaudible or duplicate recording in {path}")
        samples[name] = {"seconds": round(duration, 4), "rms": round(rms, 5), "pcmSha256": digest}
        fingerprints.add(digest)
        return data

    references = voices / "references"
    references.mkdir(exist_ok=True)
    def reference(name, source):
        data = read(name, source)
        pending = references / (name + ".tmp")
        pending.write_bytes(data)
        pending.replace(references / (name + ".wav"))

    target = voices / "Mirthwood-Echoes-Voices.zip"
    pending = target.with_suffix(".tmp")
    try:
        with zipfile.ZipFile(pending, "w", compression=zipfile.ZIP_DEFLATED) as pack:
            def write(name, data):
                info = zipfile.ZipInfo(name)
                info.compress_type = zipfile.ZIP_DEFLATED
                pack.writestr(info, data)
            for manifest in ("cast.json", "cues.json"):
                write(manifest, (voices / manifest).read_bytes())
            for v in cast:
                name = v["id"]
                folder = options.masters / "cast" / name
                reference(name, folder / "reference.wav")
                for cue in sorted(reactions):
                    member = f"voices/{name}/{cue}.wav"
                    write(member, read(member, folder / (cue + ".wav")))
            reference("narrator", options.masters / "narrator.wav")
        with zipfile.ZipFile(pending) as archive:
            if archive.testzip() or len(archive.namelist()) != 2 + len(cast)*len(reactions):
                raise ValueError("Voice pack read-back failed.")
        pending.replace(target)
    finally:
        pending.unlink(missing_ok=True)
    if options.report:
        options.report.parent.mkdir(parents=True, exist_ok=True)
        options.report.write_text(json.dumps({"cast": len(cast), "nativeEvents": len(cues["events"]),
            "reactions": len(cast)*len(reactions), "verifiedSpokenReactions": len(audited),
            "packSha256": hashlib.sha256(target.read_bytes()).hexdigest(),
            "recordings": samples}, indent=2), encoding="utf-8")
    print(f"{len(cast)} voices, {len(cast)*len(reactions)} distinct reactions and an original narrator.")


if __name__ == "__main__":
    main()
