import asyncio
import json
from datetime import datetime, timezone
from pathlib import Path

import edge_tts

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "audio" / "edge-samples"
OUT.mkdir(parents=True, exist_ok=True)

DISPLAY_TEXT = "不確定期限、履行請求か知った時。早い方から履行遅滞。物上代位、牽連性、瑕疵。"
SPOKEN_TEXT = "ふかくていきげん、りこうせいきゅうか しったとき。はやいほうから りこうちたい。ぶつじょうだいい、けんれんせい、かし。"

PREFERRED = [
    "ja-JP-NanamiNeural",
    "ja-JP-KeitaNeural",
]

async def pick_voices():
    voices = await edge_tts.list_voices()
    ja = [v for v in voices if v.get("Locale") == "ja-JP"]
    by_name = {v["ShortName"]: v for v in ja}
    picked = [by_name[n] for n in PREFERRED if n in by_name]
    if len(picked) < 2:
        for v in ja:
            if v not in picked:
                picked.append(v)
            if len(picked) == 2:
                break
    if len(picked) < 2:
        raise RuntimeError(f"Need two ja-JP voices, found {len(ja)}")
    return picked[:2]

async def main():
    voices = await pick_voices()
    items = []
    for i, v in enumerate(voices, 1):
        name = v["ShortName"]
        filename = f"edge_{i}.mp3"
        print(f"Generating {name} -> {filename}")
        communicate = edge_tts.Communicate(
            SPOKEN_TEXT,
            name,
            rate="-4%",
            volume="+0%",
            pitch="+0Hz",
        )
        await communicate.save(str(OUT / filename))
        items.append({
            "engine": "Microsoft Edge Neural TTS via edge-tts",
            "voice": name,
            "gender": v.get("Gender", ""),
            "src": f"./audio/edge-samples/{filename}",
            "note": "API keyなし・オンライン音声",
        })

    manifest = {
        "version": 1,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "displayText": DISPLAY_TEXT,
        "spokenText": SPOKEN_TEXT,
        "readingPolicy": "法律用語はひらがなで読みを固定して試聴。全件実装前の発音確認専用。",
        "items": items,
    }
    (OUT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    asyncio.run(main())
