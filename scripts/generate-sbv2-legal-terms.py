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

# pronunciation gate: terms only
TERMS = [
    ("不確定期限", "ふかくていきげん"),
    ("履行請求", "りこうせいきゅう"),
    ("履行遅滞", "りこうちたい"),
    ("物上代位", "ぶつじょうだいい"),
    ("牽連性", "けんれんせい"),
    ("瑕疵", "かし"),
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

    items = []
    for i, (term, reading) in enumerate(TERMS, 1):
        normalized = normalize_text(reading)
        phones, tones, _word2ph = g2p(normalized, use_jp_extra=True, raise_yomi_error=True)

        # Important: line_split=False is required, otherwise given_phone/given_tone
        # are ignored by Style-Bert-VITS2. This trial fixes pronunciation at phoneme level.
        sr, audio = model.infer(
            text=reading,
            language=Languages.JP,
            given_phone=phones,
            given_tone=tones,
            line_split=False,
            length=1.00,
            sdp_ratio=0.20,
            noise=0.50,
            noise_w=0.50,
            pitch_scale=1.0,
            intonation_scale=1.0,
        )

        audio16 = to_int16(np.asarray(audio))
        filename = f"term_{i:02d}.wav"
        wav_write(OUT / filename, sr, audio16)

        items.append(
            {
                "term": term,
                "reading": reading,
                "src": f"./audio/sbv2-legal-terms/{filename}",
                "phones": phones,
                "tones": tones,
            }
        )
        print(f"{term}: {reading}")
        print("  phones:", " ".join(phones))
        print("  tones :", " ".join(map(str, tones)))

    manifest = {
        "version": 1,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "engine": "Style-Bert-VITS2",
        "model": MODEL_NAME,
        "modelRepo": MODEL_REPO,
        "modelLicense": "CC BY-SA 4.0 (JVNV corpus model)",
        "testPolicy": "Six legal terms only. Each reading is fixed in hiragana and then supplied to inference with explicit given_phone and given_tone; line_split is disabled so the phoneme override is actually used.",
        "items": items,
    }
    (OUT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Generated {len(items)} pronunciation-gate samples.")


if __name__ == "__main__":
    main()
