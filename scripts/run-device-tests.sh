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
# The shared E2E scenarios run too when scripts/start-e2e-sshd.sh has said
# where its server is. adb shell hands the line to the device's sh, so the host
# key (which contains a space) is quoted once more for that shell.
E2E_ARGS=()
if [ -n "${E2E_HOST:-}" ]; then
    E2E_ARGS=(
        -e e2e_host "$E2E_HOST"
        -e e2e_port "$E2E_PORT"
        -e e2e_user "$E2E_USER"
        -e e2e_password "$E2E_PASSWORD"
        -e e2e_client_key_b64 "$E2E_CLIENT_KEY_B64"
        -e e2e_host_key "'$E2E_HOST_KEY'"
    )
fi

echo "running $INSTRUMENTATION"
adb shell am instrument -w "${E2E_ARGS[@]}" "$INSTRUMENTATION" > "$REPORT" 2>&1 || true

cat "$REPORT"

if ! grep -q "device tests passed" "$REPORT"; then
    echo "device tests did not report success" >&2
    exit 1
fi

# "device tests passed" is printed by a run that never received the server
# too, so a lost argument would otherwise pass with no E2E coverage at all.
if [ -n "${E2E_HOST:-}" ] && ! grep -Eq "E2E scenarios run: [1-9]" "$REPORT"; then
    echo "a server was configured but no E2E scenario ran" >&2
    exit 1
fi
