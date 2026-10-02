#!/bin/bash
# 自動実行を止める
LABEL="local.esthe.schedule-logger"
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
echo "✅ 自動実行を止めました（これまでの記録 ~/esthe_data はそのまま残っています）"
