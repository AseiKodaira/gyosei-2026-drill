#!/bin/bash
# 出勤スケジュールの記録を毎日決まった時刻に自動実行する設定（Mac の launchd）
# 使い方:  bash install_launchd.sh        → 毎日 21:00
#          bash install_launchd.sh 22:30  → 毎日 22:30
set -e
cd "$(dirname "$0")"
TOOL_DIR="$(pwd)"
TIME="${1:-21:00}"
HOUR=$((10#${TIME%%:*})); MINUTE=$((10#${TIME##*:}))
LABEL="local.esthe.schedule-logger"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
mkdir -p "$HOME/Library/LaunchAgents" "$HOME/esthe_data"

if [ ! -x "$TOOL_DIR/.venv/bin/python" ]; then
  echo "先に bash setup.sh を実行してください。"; exit 1
fi
case "$TOOL_DIR" in
  "$HOME/Documents"*|"$HOME/Desktop"*|"$HOME/Downloads"*)
    echo "⚠ このフォルダ（$TOOL_DIR）は Mac の保護対象のため、自動実行が失敗することがあります。"
    echo "  ~/esthe_tool などに移してから実行してください。"; exit 1;;
esac

cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$TOOL_DIR/.venv/bin/python</string>
    <string>$TOOL_DIR/schedule_logger.py</string>
  </array>
  <key>WorkingDirectory</key><string>$TOOL_DIR</string>
  <key>StartCalendarInterval</key>
  <dict>
    <key>Hour</key><integer>$HOUR</integer>
    <key>Minute</key><integer>$MINUTE</integer>
  </dict>
  <key>StandardOutPath</key><string>$HOME/esthe_data/launchd.log</string>
  <key>StandardErrorPath</key><string>$HOME/esthe_data/launchd.log</string>
</dict>
</plist>
PLIST

launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"
echo "✅ 毎日 $(printf '%02d:%02d' $HOUR $MINUTE) に自動で記録する設定をしました。"
echo "   設定ファイル: $PLIST"
echo "   結果の確認:   tail -n 20 ~/esthe_data/run_log.txt"
