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

# Rework one rejected term at a time.  The JVNV model is trained on emotional
# speech and its model card warns that even Neutral can sound expressive.
# Keep the written term, add a declarative full stop, and suppress intonation.
TERM = "不確定期限"
READING = "ふかくていきげん"
CANDIDATES = [
    {
        "id": "neutral-ordinary",
        "label": "普通読み",
        "description": "Neutral固定・抑揚35%・標準速度",
        "length": 0.90,
        "intonation_scale": 0.35,
    },
    {
        "id": "neutral-flatter",
        "label": "さらに平坦",
        "description": "Neutral固定・抑揚15%・標準速度",
        "length": 0.90,
        "intonation_scale": 0.15,
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

    synthesis_text = TERM + "。"
    normalized_surface = normalize_text(synthesis_text)
    auto_phones, auto_tones, _surface_word2ph = g2p(
        normalized_surface, use_jp_extra=True, raise_yomi_error=True
    )

    items = []
    for candidate in CANDIDATES:
        # The full stop gives the term a declarative ending.  sdp_ratio=0 removes
        # duration randomness, while post-processing suppresses pitch variation.
        sr, audio = model.infer(
            text=synthesis_text,
            language=Languages.JP,
            line_split=False,
            length=candidate["length"],
            sdp_ratio=0.0,
            noise=0.20,
            noise_w=0.0,
            style="Neutral",
            style_weight=0.0,
            pitch_scale=1.0,
            intonation_scale=candidate["intonation_scale"],
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
                "phones": auto_phones,
                "tones": auto_tones,
                "length": candidate["length"],
                "intonationScale": candidate["intonation_scale"],
            }
        )
        print(f"{candidate['label']}: {TERM} ({READING})")
        print("  phones:", " ".join(auto_phones))
        print("  tones :", " ".join(map(str, auto_tones)))

    manifest = {
        "version": 3,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "engine": "Style-Bert-VITS2",
        "model": MODEL_NAME,
        "modelRepo": MODEL_REPO,
        "modelLicense": "CC BY-SA 4.0 (JVNV corpus model)",
        "stage": "neutral-read-rework",
        "term": TERM,
        "reading": READING,
        "rejectedVersions": [1, 2],
        "rejectedReason": "Version 1 produced a non-Japanese mora length (きげーん); version 2 sounded surprised because the JVNV emotional model retained excessive intonation.",
        "testPolicy": "Compare ordinary and flatter neutral readings of one term only. The written kanji and declarative punctuation are retained, duration is deterministic, and intonation is suppressed. No remaining legal term is generated until this term passes listening review.",
        "items": items,
    }
    (OUT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Generated {len(items)} candidates for {TERM}.")


if __name__ == "__main__":
    main()
