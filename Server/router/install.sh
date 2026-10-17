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

# The tanks' shared hello token, if deploy.ps1 found one. Without it the
# server accepts any tank. An existing token is kept if none was uploaded.
mkdir -p /etc/thundergeddon
if [ -f $D/ws_token.txt ]; then
	tr -d '\r' < $D/ws_token.txt > /etc/thundergeddon/ws_token.txt
	chmod 600 /etc/thundergeddon/ws_token.txt
fi

# Keep our files when GL firmware is upgraded with "keep settings".
for f in $APP/ /etc/init.d/thundergeddon /etc/thundergeddon/; do
	grep -qxF "$f" /etc/sysupgrade.conf || echo "$f" >> /etc/sysupgrade.conf
done

rm -rf $D

/etc/init.d/thundergeddon enable
# stop + start rather than restart: on a first install there is nothing to
# stop, and restart then prints a misleading "Command failed: Not found".
/etc/init.d/thundergeddon stop 2>/dev/null || true
/etc/init.d/thundergeddon start

# Give it up to 10 s to answer (the old copy may take a moment to let go).
i=0
while [ $i -lt 10 ]; do
	sleep 1
	if wget -q -O - http://127.0.0.1:8088/api/info; then
		echo
		echo "Thundergeddon is running."
		exit 0
	fi
	i=$((i + 1))
done
echo "Thundergeddon did not answer on port 8088. Recent log:"
logread -e thundergeddon | tail -n 20
exit 1
