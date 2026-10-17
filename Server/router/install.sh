#!/bin/sh
# Runs ON THE ROUTER. deploy.ps1 uploads the server binary, the init script
# and this file to /tmp/tg-deploy, then runs this to install and (re)start.
set -e

D=/tmp/tg-deploy
APP=/opt/thundergeddon

mkdir -p $APP
# Copy then rename: the rename is atomic, so a running server is never
# left with a half-written binary.
cp $D/thundergeddon-linux-arm64 $APP/thundergeddon.new
chmod +x $APP/thundergeddon.new
mv -f $APP/thundergeddon.new $APP/thundergeddon

# Strip Windows line endings, which would break the script's first line.
tr -d '\r' < $D/thundergeddon.init > /etc/init.d/thundergeddon
chmod +x /etc/init.d/thundergeddon

# Keep our files when GL firmware is upgraded with "keep settings".
for f in $APP/ /etc/init.d/thundergeddon; do
	grep -qxF "$f" /etc/sysupgrade.conf || echo "$f" >> /etc/sysupgrade.conf
done

rm -rf $D

/etc/init.d/thundergeddon enable
# stop + start rather than restart: on a first install there is nothing to
# stop, and restart then prints a misleading "Command failed: Not found".
/etc/init.d/thundergeddon stop 2>/dev/null || true
/etc/init.d/thundergeddon start
sleep 2

if wget -q -O - http://127.0.0.1:8088/api/info; then
	echo
	echo "Thundergeddon is running."
else
	echo "Thundergeddon did not answer on port 8088. Recent log:"
	logread -e thundergeddon | tail -n 20
	exit 1
fi
