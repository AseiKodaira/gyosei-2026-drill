#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/audio/voice-comparison"
TMP="$RUNNER_TEMP/voice-comparison"
mkdir -p "$OUT" "$TMP"

DISPLAY_TEXT='不確定期限、履行請求か知った時。早い方から履行遅滞。物上代位、牽連性、瑕疵。'
SPOKEN_TEXT='ふかくていきげん、りこうせいきゅうか しったとき。はやいほうから りこうちたい。ぶつじょうだいい、けんれんせい、かし。'

cleanup() {
  docker rm -f voicevox-test aivis-test >/dev/null 2>&1 || true
}
trap cleanup EXIT

wait_http() {
  local url="$1"
  local label="$2"
  local tries="${3:-120}"
  for ((i=1; i<=tries; i++)); do
    if curl -fsS --max-time 5 "$url" >/dev/null 2>&1; then
      echo "$label is ready."
      return 0
    fi
    if (( i % 12 == 0 )); then
      echo "Waiting for $label... ($i/$tries)"
    fi
    sleep 5
  done
  echo "::error::$label did not become ready."
  return 1
}

first_style_meta() {
  python - "$1" <<'PY'
import json, sys
p=sys.argv[1]
data=json.load(open(p,encoding='utf-8'))
for speaker in data:
    styles=speaker.get('styles') or []
    if styles:
        s=styles[0]
        print(s['id'])
        print(speaker.get('name',''))
        print(s.get('name',''))
        raise SystemExit
raise SystemExit('No speaker style found')
PY
}

synth_voicevox() {
  echo "=== VOICEVOX ==="
  docker pull voicevox/voicevox_engine:cpu-latest
  docker run -d --rm --name voicevox-test -p 50021:50021 voicevox/voicevox_engine:cpu-latest >/dev/null
  wait_http "http://127.0.0.1:50021/version" "VOICEVOX" 90

  curl -fsS "http://127.0.0.1:50021/speakers" > "$TMP/voicevox-speakers.json"
  mapfile -t META < <(first_style_meta "$TMP/voicevox-speakers.json")
  VV_STYLE_ID="${META[0]}"
  VV_SPEAKER="${META[1]}"
  VV_STYLE="${META[2]}"

  curl -fsS -X POST "http://127.0.0.1:50021/audio_query" \
    --get --data-urlencode "speaker=$VV_STYLE_ID" --data-urlencode "text=$SPOKEN_TEXT" \
    > "$TMP/voicevox-query.json"

  python - "$TMP/voicevox-query.json" <<'PY'
import json,sys
p=sys.argv[1]
q=json.load(open(p,encoding='utf-8'))
q['speedScale']=1.0
q['pitchScale']=0.0
q['intonationScale']=1.08
q['volumeScale']=1.0
q['prePhonemeLength']=0.10
q['postPhonemeLength']=0.12
json.dump(q,open(p,'w',encoding='utf-8'),ensure_ascii=False)
PY

  curl -fsS -H "Content-Type: application/json" -X POST \
    --data-binary @"$TMP/voicevox-query.json" \
    "http://127.0.0.1:50021/synthesis?speaker=$VV_STYLE_ID" \
    > "$OUT/voicevox.wav"

  python - "$OUT/voicevox-meta.json" "$VV_STYLE_ID" "$VV_SPEAKER" "$VV_STYLE" <<'PY'
import json,sys
path,style_id,speaker,style=sys.argv[1:]
json.dump({
  "engine":"VOICEVOX",
  "speaker":speaker,
  "style":style,
  "styleId":int(style_id),
  "credit":f"VOICEVOX:{speaker}"
},open(path,'w',encoding='utf-8'),ensure_ascii=False,indent=2)
PY

  docker rm -f voicevox-test >/dev/null
}

synth_aivis() {
  echo "=== AivisSpeech ==="
  local DATA_DIR="$TMP/aivis-data"
  mkdir -p "$DATA_DIR"
  chmod 777 "$DATA_DIR"

  docker pull ghcr.io/aivis-project/aivisspeech-engine:cpu-latest
  docker run -d --rm --name aivis-test -p 10101:10101 \
    -v "$DATA_DIR:/home/user/.local/share/AivisSpeech-Engine-Dev" \
    ghcr.io/aivis-project/aivisspeech-engine:cpu-latest >/dev/null

  # First startup downloads the default AIVMX model (~250 MB) and BERT model (~650 MB).
  wait_http "http://127.0.0.1:10101/speakers" "AivisSpeech" 240

  curl -fsS "http://127.0.0.1:10101/speakers" > "$TMP/aivis-speakers.json"
  mapfile -t META < <(first_style_meta "$TMP/aivis-speakers.json")
  AV_STYLE_ID="${META[0]}"
  AV_SPEAKER="${META[1]}"
  AV_STYLE="${META[2]}"

  curl -fsS -X POST "http://127.0.0.1:10101/audio_query" \
    --get --data-urlencode "speaker=$AV_STYLE_ID" --data-urlencode "text=$SPOKEN_TEXT" \
    > "$TMP/aivis-query.json"

  python - "$TMP/aivis-query.json" <<'PY'
import json,sys
p=sys.argv[1]
q=json.load(open(p,encoding='utf-8'))
q['speedScale']=1.0
q['pitchScale']=0.0
q['intonationScale']=1.0
q['tempoDynamicsScale']=1.20
q['volumeScale']=1.0
json.dump(q,open(p,'w',encoding='utf-8'),ensure_ascii=False)
PY

  curl -fsS -H "Content-Type: application/json" -X POST \
    --data-binary @"$TMP/aivis-query.json" \
    "http://127.0.0.1:10101/synthesis?speaker=$AV_STYLE_ID" \
    > "$OUT/aivis.wav"

  python - "$OUT/aivis-meta.json" "$AV_STYLE_ID" "$AV_SPEAKER" "$AV_STYLE" <<'PY'
import json,sys
path,style_id,speaker,style=sys.argv[1:]
json.dump({
  "engine":"AivisSpeech",
  "speaker":speaker,
  "style":style,
  "styleId":int(style_id),
  "credit":f"AivisSpeech / {speaker}"
},open(path,'w',encoding='utf-8'),ensure_ascii=False,indent=2)
PY

  docker rm -f aivis-test >/dev/null
}

synth_voicevox
synth_aivis

python - "$OUT" "$DISPLAY_TEXT" "$SPOKEN_TEXT" <<'PY'
import json,sys,os
from datetime import datetime, timezone
out,display,spoken=sys.argv[1:]
vv=json.load(open(os.path.join(out,'voicevox-meta.json'),encoding='utf-8'))
av=json.load(open(os.path.join(out,'aivis-meta.json'),encoding='utf-8'))
manifest={
  "version":1,
  "generatedAt":datetime.now(timezone.utc).isoformat(),
  "displayText":display,
  "spokenText":spoken,
  "readingPolicy":"The comparison audio uses an explicit hiragana reading. It does not rely on the engines guessing legal-term readings.",
  "items":[
    {**av,"src":"./audio/voice-comparison/aivis.wav","rhythm":"tempoDynamicsScale 1.20"},
    {**vv,"src":"./audio/voice-comparison/voicevox.wav","rhythm":"intonationScale 1.08"}
  ]
}
json.dump(manifest,open(os.path.join(out,'manifest.json'),'w',encoding='utf-8'),ensure_ascii=False,indent=2)
PY

rm -f "$OUT/aivis-meta.json" "$OUT/voicevox-meta.json"
echo "Generated AivisSpeech + VOICEVOX comparison audio."
