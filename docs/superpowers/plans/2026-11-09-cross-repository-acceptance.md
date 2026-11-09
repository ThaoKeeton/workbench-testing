# Cross-repository source acceptance implementation plan

**Goal:** Verify the migrated synthetic source fixture in four isolated Windows host processes while binding the independently committed host, SDK and testing plugin.

**Architecture:** Preserve the production SDK pin and introduce an explicitly selected acceptance-host pin. Validate that the acceptance host's SDK sources match the existing SDK lock, build the plugin against the original SDK packages, and bind both repositories, package hashes and actual host binaries in fixture provenance. Extend the native host's synthetic probe and testing-repository report validator with a strict cross-repository receipt while retaining the host's existing same-repository lane.

**Technology:** .NET 10, PowerShell 7.6, Python 3, GitHub-hosted Windows Actions.

**Constraints:** Production plugin ID, API 1, SDK 0.4.0 and project data schema 1 stay unchanged. No user computers, real webhooks, DingTalk sends, live providers, firewall changes or elevation changes. Source fixture is synthetic, opt-in and not a production plugin.

## Tasks

1. Add a testable host-side provenance validator; test independent commits, host/SDK/package hashes, wrong identities, duplicate fields and tampering. Preserve the existing legacy probe path.
2. Pin the host change by its actual commit in the testing repository, verify that its SDK source bytes equal the existing 0.4.0 lock, and separate the SDK source commit from the acceptance host commit throughout delivery verification.
3. Extend fixture provenance and the native report/runner to bind plugin, host, SDK package and host DLL hashes across all four phases; keep the existing report format valid for the legacy same-repository lane.
4. Add a public Windows Actions job that builds both host versions separately, packages the synthetic fixture against the original SDK and pinned acceptance host, and runs seed, verify, stop-figma and stop-ai with in-memory HTTP. Keep all four process/lease/evidence checks and upload only synthetic evidence.
5. Run focused tests on Linux, reference compilation and public Windows Actions; update both draft PRs and report any missing verification without calling the migration finished prematurely.
