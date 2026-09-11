# Spec: algorithm negotiation for keystore-backed signers

**Repository:** `Sniperlyf3/meowshell`
**Affects:** `cmd/meowshell/agentauth.go`, `cmd/meowshell/protocol.go`, `dotnet/Meowshell/MeowshellAgentConnection.cs`
**Status:** proposed

## Summary

`keystoreSigner` implements `ssh.Signer` but not `ssh.AlgorithmSigner`, and hardcodes
the signature format to the key's own type. For RSA keys that type is `ssh-rsa`,
which means SHA-1 — disabled by default in OpenSSH since 8.8 (2021). The result is
that a hardware-backed RSA key is offered to the server in a form every current
server rejects.

This spec proposes implementing `ssh.MultiAlgorithmSigner` so the negotiated
signature algorithm reaches the signing callback, and the callback can advertise
what the underlying key is actually able to do.

## Why this matters

Android's Keystore is the motivating case. AOSP requires every KeyMint
implementation to provide ECDSA (NIST P-224/256/384/521) and RSA (2048/3072/4096).
Curve25519 is optional and largely absent from secure elements, so on a given
device the hardware-backed options are realistically ECDSA and RSA.

Of those two, only ECDSA works today:

| Key type | `pub.Type()` | Valid SSH signature algorithms | Works today? |
| --- | --- | --- | --- |
| ECDSA P-256 | `ecdsa-sha2-nistp256` | `ecdsa-sha2-nistp256` | Yes — format string equals key type |
| Ed25519 | `ssh-ed25519` | `ssh-ed25519` | Yes — same reason |
| RSA | `ssh-rsa` | `rsa-sha2-512`, `rsa-sha2-256`, `ssh-rsa` | **No** — only ever offers SHA-1 |

Devices whose only hardware-backed option is RSA therefore get no usable
hardware-backed key at all. That includes a meaningful share of older devices and
some StrongBox implementations.

## Current behaviour

`cmd/meowshell/agentauth.go`:

```go
func (s *keystoreSigner) Sign(_ io.Reader, data []byte) (*ssh.Signature, error) {
	resp, err := s.session.prompt(controlMessage{
		PromptKind: "sign",
		KeyID:      s.keyID,
		Algorithm:  s.pub.Type(),   // always the key type
		SignData:   data,
	})
	...
	return &ssh.Signature{Format: s.pub.Type(), Blob: resp.Signature}, nil
}
```

`golang.org/x/crypto/ssh` selects a signature algorithm during public-key auth by
testing whether the signer implements `AlgorithmSigner`. When it does not, the
signer is wrapped and only the key's own algorithm is ever used — regardless of
what the server advertised in its `server-sig-algs` extension. That wrapping is
why the SHA-1 form is the only one this signer can produce.

## Proposed change

### 1. Implement `MultiAlgorithmSigner`

`AlgorithmSigner` alone is not sufficient. Its contract says a signer "should be
prepared to be invoked with every algorithm supported by the public key format",
which a keystore key cannot honour — the hardware holds one key of one type, and
the app has no way to produce, say, an Ed25519 signature from a P-256 key.
`MultiAlgorithmSigner` adds `Algorithms() []string`, letting the signer state in
preference order what it can actually do, so negotiation never selects something
unsatisfiable.

```go
// Algorithms reports the signature algorithms this key can produce, in
// preference order. A keystore key is a single key of a single type, so this is
// derived from the public key rather than being a free choice.
func (s *keystoreSigner) Algorithms() []string {
	switch s.pub.Type() {
	case ssh.KeyAlgoRSA:
		// SHA-2 first. ssh-rsa is retained only for servers predating RFC 8332;
		// OpenSSH has refused it by default since 8.8.
		return []string{ssh.KeyAlgoRSASHA512, ssh.KeyAlgoRSASHA256, ssh.KeyAlgoRSA}
	default:
		return []string{s.pub.Type()}
	}
}

// SignWithAlgorithm signs under the algorithm the handshake negotiated, rather
// than assuming the key's own type. For RSA those differ, and the difference is
// which digest the signature commits to.
func (s *keystoreSigner) SignWithAlgorithm(_ io.Reader, data []byte, algorithm string) (*ssh.Signature, error) {
	if algorithm == "" {
		algorithm = s.Algorithms()[0]
	}
	if !slices.Contains(s.Algorithms(), algorithm) {
		return nil, fmt.Errorf("keystore key %q cannot sign with %q", s.keyID, algorithm)
	}

	resp, err := s.session.prompt(controlMessage{
		PromptKind: "sign",
		KeyID:      s.keyID,
		Algorithm:  algorithm,   // the negotiated algorithm, not the key type
		SignData:   data,
	})
	if err != nil {
		return nil, err
	}
	if resp.Cancelled || len(resp.Signature) == 0 {
		return nil, fmt.Errorf("keystore signing for key %q was refused", s.keyID)
	}
	return &ssh.Signature{Format: algorithm, Blob: resp.Signature}, nil
}

// Sign keeps the plain Signer contract working, at the key's default algorithm.
func (s *keystoreSigner) Sign(rand io.Reader, data []byte) (*ssh.Signature, error) {
	return s.SignWithAlgorithm(rand, data, "")
}
```

`Format` must be the negotiated algorithm, not the key type. A signature computed
over SHA-512 but labelled `ssh-rsa` fails verification.

### 2. Protocol semantics

No new fields. `controlMessage.Algorithm` already exists and already reaches the
.NET client as `MeowshellSignRequest.Algorithm`.

What changes is its meaning: today it carries the *key type*; afterwards it carries
the *signature algorithm*. For ECDSA and Ed25519 those are the same string, so
existing handlers see no change. Only RSA keys produce values that did not occur
before (`rsa-sha2-256`, `rsa-sha2-512`).

This is worth stating explicitly in `dotnet/README.md`, because a handler that
ignores `Algorithm` and always computes `SHA256withRSA` will now be wrong half the
time — and wrong in a way that surfaces only as a failed handshake.

### 3. Signature blob formats (normative)

The single most likely implementation error is returning the wrong bytes. The
`Blob` is the algorithm-specific signature blob, not a DER envelope and not the
full SSH signature structure.

| Algorithm | `Blob` contents | Android `Signature` algorithm | Conversion needed |
| --- | --- | --- | --- |
| `rsa-sha2-256` | Raw PKCS#1 v1.5 signature, length = modulus size | `SHA256withRSA` | None |
| `rsa-sha2-512` | Raw PKCS#1 v1.5 signature, length = modulus size | `SHA512withRSA` | None |
| `ssh-rsa` | Raw PKCS#1 v1.5 signature, length = modulus size | `SHA1withRSA` | None |
| `ecdsa-sha2-nistp256` | `mpint r \|\| mpint s` (RFC 5656 §3.1.2) | `SHA256withECDSA` | **Yes — see below** |
| `ssh-ed25519` | Raw 64-byte signature | n/a | None |

Each `mpint` is a 4-byte big-endian length followed by the two's-complement
big-endian value, with a leading `0x00` when the high bit would otherwise be set.

Android returns ECDSA signatures as a DER `SEQUENCE { INTEGER r, INTEGER s }`.
Passing that through unconverted produces a signature the server rejects with no
indication of why, so the client must decode the DER and re-encode as two mpints.
That conversion is client-side work and is out of scope for this repository, but it
belongs in the documentation beside this table.

### 4. Errors

An algorithm outside `Algorithms()` should fail before any prompt is sent, so a
misconfigured client does not surface as a user-visible biometric prompt that then
fails. Reuse the existing `errAuthFailed` code when reporting it outward.

## Tests

Unit, in `cmd/meowshell/agentauth_test.go`:

- `SignWithAlgorithm` forwards the negotiated algorithm in `controlMessage.Algorithm`
  and sets `Signature.Format` to the same value.
- An empty algorithm resolves to the key's first preference.
- An algorithm not in `Algorithms()` is rejected without a prompt being sent.
- `Algorithms()` returns SHA-2 before `ssh-rsa` for an RSA key, and exactly one
  entry for ECDSA and Ed25519 keys.

End-to-end, alongside the existing `agent_auth_e2e_test.go`:

- An RSA keystore-backed key authenticates against an sshd configured with
  `PubkeyAcceptedAlgorithms rsa-sha2-256,rsa-sha2-512`. This is the test that
  actually proves the defect is fixed: it fails on the current code, because the
  only algorithm offered is one the server does not accept.
- The existing ECDSA path still authenticates unchanged, confirming no regression
  for the case that works today.

## Out of scope

- Ed25519 keystore keys. Curve25519 is optional in KeyMint and absent from most
  secure elements; nothing here depends on it, and `Algorithms()` handles it
  correctly if a device does provide it.
- FIDO/`sk-*` key types.
- Certificate signers. `Algorithms()` must not return certificate algorithm names,
  per the `MultiAlgorithmSigner` contract.

## References

- [RFC 8332](https://www.rfc-editor.org/rfc/rfc8332) — `rsa-sha2-256` / `rsa-sha2-512`
- [RFC 5656 §3.1.2](https://www.rfc-editor.org/rfc/rfc5656#section-3.1.2) — ECDSA signature blob
- [`ssh.MultiAlgorithmSigner`](https://pkg.go.dev/golang.org/x/crypto/ssh#MultiAlgorithmSigner)
- [Android Keystore features](https://source.android.com/docs/security/features/keystore/features) — required algorithms
