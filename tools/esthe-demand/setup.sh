#!/bin/bash
# 初回だけ実行：このフォルダに専用の Python 環境（.venv）を作り、必要な部品を入れる
set -e
cd "$(dirname "$0")"
echo "▶ 専用の Python 環境を作ります（.venv）"
/usr/bin/python3 -m venv .venv
echo "▶ 部品をインストールします（数分かかります）"
.venv/bin/python -m pip install --quiet --upgrade pip
.venv/bin/python -m pip install --quiet -r requirements.txt
echo "▶ 確認"
.venv/bin/python - <<'PY'
import ssl, urllib3, requests, pandas, matplotlib, pytrends
print("  SSL:", ssl.OPENSSL_VERSION)
print("  urllib3:", urllib3.__version__, "(1.x 系なら OK)")
print("  requests:", requests.__version__)
r = requests.get("https://www.google.com", timeout=15)
print("  通信テスト:", r.status_code, "OK" if r.ok else "NG")
PY
echo "✅ 準備ができました"
