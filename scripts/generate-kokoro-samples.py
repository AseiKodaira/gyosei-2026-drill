from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import soundfile as sf
from kokoro import KPipeline
from misaki import ja

ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "audio" / "kokoro-samples"
MANIFEST = OUT_DIR / "manifest.json"

DISPLAY_TEXT = "不確定期限、履行請求か知った時。早い方から履行遅滞。"
# Trial audio uses an explicit reading so the voice comparison is not polluted by legal-term misreading.
SPOKEN_TEXT = "ふかくていきげん、りこうせいきゅうか しったとき。はやいほうから りこうちたい。"

VOICES = [
    ("jf_alpha", "Alpha", "女性"),
    ("jf_gongitsune", "Gongitsune", "女性"),
    ("jf_nezumi", "Nezumi", "女性"),
    ("jf_tebukuro", "Tebukuro", "女性"),
    ("jm_kumo", "Kumo", "男性"),
]

SAMPLE_RATE = 24000
SPEED = 0.96


def render_voice(pipeline: KPipeline, voice: str) -> np.ndarray:
    chunks: list[np.ndarray] = []
    for result in pipeline(SPOKEN_TEXT, voice=voice, speed=SPEED):
        if result.audio is None:
            continue
        audio = result.audio.detach().cpu().numpy().astype(np.float32)
        chunks.append(audio)
    if not chunks:
        raise RuntimeError(f"No audio generated for {voice}")
    if len(chunks) == 1:
        return chunks[0]
    # Small natural pause only when the pipeline itself split the sentence.
    silence = np.zeros(int(SAMPLE_RATE * 0.12), dtype=np.float32)
    joined: list[np.ndarray] = []
    for i, chunk in enumerate(chunks):
        if i:
            joined.append(silence)
        joined.append(chunk)
    return np.concatenate(joined)


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    # KPipeline defaults to the older fugashi/cutlet Japanese G2P. For this
    # trial, force Misaki's newer pyopenjtalk path before KPipeline is created.
    # This avoids a separate MeCab dictionary download and keeps pitch accent data.
    original_jag2p = ja.JAG2P
    ja.JAG2P = lambda *args, **kwargs: original_jag2p(version="pyopenjtalk")
    try:
        pipeline = KPipeline(lang_code="j")
    finally:
        ja.JAG2P = original_jag2p

    manifest = {
        "version": 1,
        "engine": "Kokoro-82M",
        "repo": "hexgrad/Kokoro-82M",
        "sampleRate": SAMPLE_RATE,
        "speed": SPEED,
        "displayText": DISPLAY_TEXT,
        "spokenText": SPOKEN_TEXT,
        "note": "Trial only. Legal-term readings are explicitly supplied for this sample.",
        "voices": [],
    }

    for voice, label, gender in VOICES:
        print(f"Generating {voice}...")
        audio = render_voice(pipeline, voice)
        filename = f"{voice}.wav"
        sf.write(OUT_DIR / filename, audio, SAMPLE_RATE, subtype="PCM_16")
        manifest["voices"].append(
            {
                "id": voice,
                "label": label,
                "gender": gender,
                "src": f"./audio/kokoro-samples/{filename}",
            }
        )

    MANIFEST.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Wrote {MANIFEST}")
    print(f"Generated {len(VOICES)} Kokoro Japanese voice samples.")


if __name__ == "__main__":
    main()
