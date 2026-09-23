from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from huggingface_hub import hf_hub_download
from scipy.io.wavfile import write as wav_write

from style_bert_vits2.constants import Languages
from style_bert_vits2.nlp import bert_models
from style_bert_vits2.nlp.japanese.g2p import g2p
from style_bert_vits2.nlp.japanese.normalizer import normalize_text
from style_bert_vits2.tts_model import TTSModel

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "audio" / "sbv2-legal-terms"
ASSETS = ROOT / ".cache" / "sbv2-model-assets"
OUT.mkdir(parents=True, exist_ok=True)
ASSETS.mkdir(parents=True, exist_ok=True)

MODEL_REPO = "litagin/style_bert_vits2_jvnv"
MODEL_NAME = "jvnv-F1-jp"
MODEL_FILE = f"{MODEL_NAME}/jvnv-F1-jp_e160_s14000.safetensors"
CONFIG_FILE = f"{MODEL_NAME}/config.json"
STYLE_FILE = f"{MODEL_NAME}/style_vectors.npy"

# Rework one rejected term at a time.  The first trial passed a hiragana-only
# string to the Japanese BERT encoder.  That preserved the phoneme sequence but
# discarded the lexical context of the written term and produced a non-Japanese
# mora length ("きげーん").  Keep the surface kanji as model input and compare
# deterministic variants before generating any of the remaining five terms.
TERM = "不確定期限"
READING = "ふかくていきげん"
CANDIDATES = [
    {
        "id": "surface-auto-normal",
        "label": "候補A",
        "description": "漢字本文・自動読み／アクセント・標準速度",
        "fix_reading": False,
        "length": 0.90,
    },
    {
        "id": "surface-fixed-normal",
        "label": "候補B",
        "description": "漢字本文・読み／アクセント固定・標準速度",
        "fix_reading": True,
        "length": 0.90,
    },
    {
        "id": "surface-fixed-compact",
        "label": "候補C",
        "description": "漢字本文・読み／アクセント固定・短めの拍",
        "fix_reading": True,
        "length": 0.76,
    },
]


def download_assets() -> tuple[Path, Path, Path]:
    paths = []
    for filename in (MODEL_FILE, CONFIG_FILE, STYLE_FILE):
        p = hf_hub_download(MODEL_REPO, filename, local_dir=str(ASSETS))
        paths.append(Path(p))
    return paths[0], paths[1], paths[2]


def prepare_bert() -> None:
    repo_id = "ku-nlp/deberta-v2-large-japanese-char-wwm"
    bert_models.load_model(Languages.JP, repo_id)
    bert_models.load_tokenizer(Languages.JP, repo_id)


def to_int16(audio: np.ndarray) -> np.ndarray:
    data = np.asarray(audio)
    if data.dtype == np.int16:
        return data
    if np.issubdtype(data.dtype, np.floating):
        peak = float(np.max(np.abs(data))) if data.size else 0.0
        if peak > 0:
            data = data / peak
        return np.clip(data * 32767.0, -32768, 32767).astype(np.int16)
    if data.dtype == np.int32:
        return (data / 65536).astype(np.int16)
    return np.clip(data, -32768, 32767).astype(np.int16)


def main() -> None:
    prepare_bert()
    model_path, config_path, style_path = download_assets()
    model = TTSModel(
        model_path=model_path,
        config_path=config_path,
        style_vec_path=style_path,
        device="cpu",
    )
    model.load()

    normalized_reading = normalize_text(READING)
    fixed_phones, fixed_tones, _word2ph = g2p(
        normalized_reading, use_jp_extra=True, raise_yomi_error=True
    )
    normalized_surface = normalize_text(TERM)
    auto_phones, auto_tones, _surface_word2ph = g2p(
        normalized_surface, use_jp_extra=True, raise_yomi_error=True
    )

    items = []
    for candidate in CANDIDATES:
        fix_reading = candidate["fix_reading"]
        phones = fixed_phones if fix_reading else auto_phones
        tones = fixed_tones if fix_reading else auto_tones

        # The model must receive the written Japanese term so its BERT features
        # retain lexical context.  sdp_ratio=0 makes phoneme duration deterministic;
        # this comparison changes only reading control and overall pace.
        sr, audio = model.infer(
            text=TERM,
            language=Languages.JP,
            given_phone=phones if fix_reading else None,
            given_tone=tones if fix_reading else None,
            line_split=False,
            length=candidate["length"],
            sdp_ratio=0.0,
            noise=0.20,
            noise_w=0.0,
            pitch_scale=1.0,
            intonation_scale=1.0,
        )

        audio16 = to_int16(np.asarray(audio))
        filename = f"term_01_{candidate['id']}.wav"
        wav_write(OUT / filename, sr, audio16)

        items.append(
            {
                "id": candidate["id"],
                "label": candidate["label"],
                "description": candidate["description"],
                "src": f"./audio/sbv2-legal-terms/{filename}",
                "phones": phones,
                "tones": tones,
                "length": candidate["length"],
            }
        )
        print(f"{candidate['label']}: {TERM} ({READING})")
        print("  phones:", " ".join(phones))
        print("  tones :", " ".join(map(str, tones)))

    manifest = {
        "version": 2,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "engine": "Style-Bert-VITS2",
        "model": MODEL_NAME,
        "modelRepo": MODEL_REPO,
        "modelLicense": "CC BY-SA 4.0 (JVNV corpus model)",
        "stage": "single-term-rework",
        "term": TERM,
        "reading": READING,
        "rejectedVersion": 1,
        "rejectedReason": "The hiragana-only model input produced a non-Japanese mora length: きげーん.",
        "testPolicy": "Compare one rejected term only. The written kanji is retained as model input, duration prediction is deterministic, and no remaining legal term is generated until this term passes listening review.",
        "items": items,
    }
    (OUT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Generated {len(items)} candidates for {TERM}.")


if __name__ == "__main__":
    main()
