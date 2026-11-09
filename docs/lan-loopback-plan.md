# Loopback dispatch foundation plan

**Goal:** Prove a bounded synthetic test request and result over a real authenticated local connection without accessing another computer.

**Architecture:** An explicitly started loopback-only agent accepts one synthetic comparison request per TLS connection. A client pins the agent certificate, the agent pins the explicitly authorized client certificate, and an in-memory ledger refuses repeated request IDs within the agent lifetime. Neither endpoint retries automatically after an uncertain transmission.

**Tech stack:** .NET 10, `SslStream`, xUnit; no new third-party runtime package.

**Spec:** [LAN multi-device design](lan-multidevice-design.md), proposed and not yet implemented.

**Global constraints:** Bind and connect only to loopback; accept only bounded synthetic comparisons; no screen capture, input injection, real device discovery, firewall or driver changes; preserve SDK 0.4.0/API 1 and schema 1. This is not a usable remote-desktop feature or a claim of persistent pairing.

**Review focus:** Wrong server certificate; unauthorized or revoked client; duplicate task ID; truncated or oversized frame; disconnect after transmission must produce unknown, never an automatic replay.

1. Add tests that exercise actual TLS over ephemeral loopback ports for an authorized completed comparison and explicit failures. Observe the tests fail before implementation.
2. Implement bounded framing, pinned mutual certificate authentication, explicit dispatch grant, synthetic execution, duplicate detection, cancellation and deterministic cleanup. No real test executor or arbitrary command/path is allowed.
3. Run the focused tests locally, then run them on Linux and Windows in public Actions. Report exact limits and keep cross-machine execution disabled until persistent device identity, authorization, recovery, UI and host integration are designed and verified.
