#!/usr/bin/env bash
#
# Installs the device-test APK on whatever device or emulator adb can see, runs
# it, and fails if the run did not report success.
#
# A file rather than lines in the workflow because the emulator action executes
# its "script:" input one line at a time, each in a separate "sh -c". Nothing
# carries between lines -- not "set -eu", not a variable, not an "if" -- so a
# multi-line script there is split apart and fails to parse. One line invoking
# this is the only shape that works, and it has the side benefit of being
# runnable by hand against a plugged-in phone.
set -euo pipefail

PACKAGE=dev.sniperlyf3.meowssh.devicetests
INSTRUMENTATION="$PACKAGE/$PACKAGE.TestInstrumentation"
REPORT=${REPORT:-instrumentation.txt}

APK=${1:-$(find tests/MeowSSH.Device.Tests/bin -name "*.devicetests-Signed.apk" | head -1)}
if [ -z "$APK" ] || [ ! -f "$APK" ]; then
    echo "no device test APK found; build tests/MeowSSH.Device.Tests first" >&2
    exit 1
fi

echo "installing $APK"
adb install -r "$APK"

# "am instrument -w" blocks until the instrumentation calls Finish, but its own
# exit status is 0 whatever the tests did. The report it prints is therefore the
# only thing worth believing, which is why it is captured and checked rather
# than trusted to set a status.
echo "running $INSTRUMENTATION"
adb shell am instrument -w "$INSTRUMENTATION" > "$REPORT" 2>&1 || true

cat "$REPORT"

if ! grep -q "device tests passed" "$REPORT"; then
    echo "device tests did not report success" >&2
    exit 1
fi
