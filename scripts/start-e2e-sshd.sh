#!/usr/bin/env bash
#
# Starts the OpenSSH server the on-device E2E scenarios connect to, and exports
# how to reach it for scripts/run-device-tests.sh.
#
# It runs on the CI runner, outside the emulator: the emulator reaches the
# runner's loopback as 10.0.2.2, so the phone side connects over the emulator's
# real network stack to a real sshd, the same way it would reach a server on
# a LAN. Mirrors tests/MeowSSH.Integration.Tests/SshServerFixture.cs, which is
# the same server for the Linux run; keep the two in step.
#
# Needs root (useradd, sshd). Writes the E2E_* variables to $GITHUB_ENV when
# that is set, and prints them as exports otherwise, so it also works by hand:
#   eval "$(sudo ./scripts/start-e2e-sshd.sh)"
set -euo pipefail

PORT=${E2E_SSHD_PORT:-2222}
USERNAME=meowtest
PASSWORD=meow-correct-horse
ROOT=${E2E_SSHD_ROOT:-${RUNNER_TEMP:-/tmp}/meowssh-e2e-sshd}
# What the device dials. 10.0.2.2 is the emulator's alias for the host's loopback.
DEVICE_HOST=${E2E_DEVICE_HOST:-10.0.2.2}

rm -rf "$ROOT"
mkdir -p "$ROOT"
ssh-keygen -q -t ed25519 -f "$ROOT/host_ed25519" -N "" -C meowssh-e2e-host
ssh-keygen -q -t ed25519 -f "$ROOT/client_key" -N "" -C meowssh-e2e-client

if ! id "$USERNAME" >/dev/null 2>&1; then
    useradd -m -s /bin/bash "$USERNAME"
fi
echo "$USERNAME:$PASSWORD" | chpasswd

SSH_DIR="/home/$USERNAME/.ssh"
mkdir -p "$SSH_DIR"
cp "$ROOT/client_key.pub" "$SSH_DIR/authorized_keys"
chown -R "$USERNAME:$USERNAME" "$SSH_DIR"
chmod 700 "$SSH_DIR"
chmod 600 "$SSH_DIR/authorized_keys"

cat > "$ROOT/sshd_config" <<CONFIG
Port $PORT
ListenAddress 127.0.0.1
HostKey $ROOT/host_ed25519
PidFile $ROOT/sshd.pid
LogLevel VERBOSE
UsePAM no
PasswordAuthentication yes
PubkeyAuthentication yes
PermitRootLogin no
AllowUsers $USERNAME
StrictModes no
Subsystem sftp /usr/lib/openssh/sftp-server
CONFIG

mkdir -p /run/sshd
/usr/sbin/sshd -f "$ROOT/sshd_config" -E "$ROOT/sshd.log"

for _ in $(seq 1 100); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then break; fi
    sleep 0.2
done
(exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null || { echo "sshd did not start on $PORT" >&2; cat "$ROOT/sshd.log" >&2; exit 1; }

# Read by run-device-tests.sh and passed to the instrumentation as -e e2e_*.
emit() {
    if [ -n "${GITHUB_ENV:-}" ]; then echo "$1=$2" >> "$GITHUB_ENV"; else printf 'export %s=%q\n' "$1" "$2"; fi
}
emit E2E_HOST "$DEVICE_HOST"
emit E2E_PORT "$PORT"
emit E2E_USER "$USERNAME"
emit E2E_PASSWORD "$PASSWORD"
emit E2E_CLIENT_KEY_B64 "$(base64 -w0 "$ROOT/client_key")"
emit E2E_HOST_KEY "$(cut -d' ' -f1,2 "$ROOT/host_ed25519.pub")"
emit E2E_SSHD_LOG "$ROOT/sshd.log"
echo "E2E sshd listening on 127.0.0.1:$PORT (device dials $DEVICE_HOST:$PORT)" >&2
