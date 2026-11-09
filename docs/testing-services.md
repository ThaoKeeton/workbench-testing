# Testing services 1 — host 0.4.0

The app and both SDK packages are preview **0.4.0**, file version **0.4.0.0**.
Assembly identity **1.0.0.0**, API 1, runtime services 1, project services 1 and
the existing context/WinUiView signatures stay compatible.
`WorkbenchTestingServices.Version` is 1. `IPluginTestingContext` derives from
`IPluginProjectContext` and adds only `IProjectTestWorkspace Testing`.

The host implements the optional testing context with guarded source/case
storage, durable run journals, passive evidence and retained owner initialization.
The independent native testing plugin now consumes these services through the portable
controller and same-window code controls; see [installation and exact source/runtime limits](testing-plugin.md).
Existing preset case-list plugins remain examples. Consumers should check the optional derived interface
and declare `minimumHostVersion: "0.4.0"` when using testing services. Existing
project-only plugins may retain 0.3.0; frozen business packages retain their
original 0.2.0 minimum and bytes.

The exact public declarations are in
[TestingContracts.cs](../src/Workbench.Sdk/TestingContracts.cs). Every workspace
method ends in `Async`, returns a specific snapshot/summary/evidence type, and
has a final optional `CancellationToken`. Source/case writes take drafts or
host-ID-bearing case content, never caller-selected paths, project identities,
producer identities or run/session IDs. The captured-identity facade
supplies the first `producerPluginId` parameter used by Core write methods.
Cancellation does not establish rollback; after an admitted operation settles,
reread its actual durable state.

## Committed observed text state

`TestRunEventDraft` and `TestRunEvent` each add the nullable init-only property
`string? NativePlainTextState`; their positional constructors and deconstruction
shapes remain unchanged. StepFinished requires the actual
`TestActionResult.NativePlainTextState`: 1–8192 UTF-16 code units, valid Unicode,
preserved exactly, including astral characters, CRLF, tabs and whitespace-only
state. No normalization or truncation applies. Every other event kind requires
null; an unfinished step has no invented text or result.

The schema1 event field `nativePlainTextState` participates in the exact
stored-event bytes and journal hash, within the existing 256 KiB encoded-event
cap. AppendRunEvent must commit the adapter's observed text, and ReadRun must
return that committed text after reopen without re-executing the adapter or
substituting planned action values. This intended user-visible native text is
separate from allow-listed diagnostic ErrorCode and must not contain raw
exception messages. It remains readable when the simulated BMP glyph renderer
cannot reproduce arbitrary Unicode.

Real IO tests append and reopen independently chosen multiline/astral/tabs,
whitespace-only and exact 8192-unit state, checking exact text, file bytes and
hash. Invalid Unicode, null/empty/oversized state and non-null state on another
event kind refuse without changing existing bytes. The native plugin renders this committed text separately from its BMP, including
Unicode omitted by the bounded glyph renderer. Actual shipping-EXE reopened
rendering/decoder probes are implemented; current Windows execution remains a
separate pending gate until the current source's reports pass.

## Implemented source and case behavior

`ProjectTestStore(userRoot, projectId)` captures a normalized project root and
valid project identity (`default` or a managed 32-character lower-hex identity).
An existing `.workbench-project/workspace.json` must also match that captured
identity before testing reads or initialization. Missing legacy markers are
permitted; the testing store always supplies its own strict project binding.
It implements the source/case methods below and every run/evidence store method
in the SDK shape, with a validated `producerPluginId` first on Core mutations.
`ProjectRuntimeScope.Testing` owns one store, and each real registration context
exposes its own captured `IProjectTestWorkspace` facade.

PublishSource creates a new immutable source with host ID/project/producer/UTC,
copied common items and exact original bytes. `OriginalSha256` hashes those exact
bytes, including a UTF-8 BOM when present; `Revision` separately hashes the exact
stored JSON envelope bytes. Titles, document IDs and locators are literal data,
including a locator such as `../../x`. They never form filesystem paths.
Provider metadata declares a mapping; it is not cryptographic proof of any
remote document. Builtin local source parsing/generation is implemented in the independent portable
domain plugin; the generic store does not parse provider wire formats.

CreateCaseSet mints new case/step IDs. A fork uses drafts and receives new IDs
even when it preserves another producer's exact traces. Every case and step has
1–16 traces; each trace must match an existing source's exact revision, literal
locator and zero-based criterion index. Updates require the actual prior
revision and the original producer, case/step membership, order and traces.
Only metadata and instruction/expected text may change. A text edit requires
that step's action to become null. Metadata alone preserves its exact action;
changing or introducing a binding requires a new generated set.

Allowed typed actions are `SetText(task.title, value)`,
`Click(task.save, null)`, and
`AssertText(task.toast|task.validation|task.createdCount, value)`.
SetText permits empty text for negative examples. Values are bounded to 4096
UTF-16 code units. No arbitrary selector, process, shell, network or OS input is
represented. Manual steps cannot enter an executable plan. Core commits records;
adapter execution is implemented by the independent supervised domain runner.

ReadCaseRevision returns the matching current revision or that exact immutable
archive. It never falls back to another revision. The internal freeze helper
copies current raw envelope bytes under the already-held lease before a
referencing run is published. Ordinary autosaves do not create history.

## Storage schema 1

All files are under `<userRoot>/.workbench-project/testing/`:

```text
store.json                              {schemaVersion, projectId}
sources/<sourceId>.json                  immutable SourceEnvelope
cases/<setId>.json                       current CAS CaseEnvelope
case-revisions/<setId>/<revision>.json    exact immutable CaseEnvelope bytes
runs/<runId>/plan.json                   immutable PlanEnvelope
runs/<runId>/head.json                   authoritative committed sequence/digest
runs/<runId>/events/0000000000.json       host Queued; contiguous D10 sequence
runs/<runId>/evidence/<evidenceId>/receipt.json
runs/<runId>/evidence/<evidenceId>/frame.bmp
```

The cooperative exclusive lease is the adjacent
`.workbench-project/testing.lock` regular empty file. Missing reads create no
root, metadata directory, testing directory, lease or document. Writers acquire
the lease before initialization. An existing partial root without its complete
schema is refused and preserved, including after interrupted initialization.
An empty archive directory is likewise incomplete and refused. Valid complete
unreferenced archives are retained because run-plan publication may follow
archive publication. No automatic pruning occurs.

JSON is UTF-8, camelCase, exact required fields, maximum depth 32. Enums use exact
case-sensitive SDK member names, never numeric values. SourceEnvelope fields:
`schemaVersion,id,projectId,producerPluginId,createdUtc,kind,providerId,providerVersion,documentId,title,originalBytes,originalSha256,items`.
`originalBytes` is Base64. CaseEnvelope fields:
`schemaVersion,id,projectId,producerPluginId,updatedUtc,name,cases`.
Nested item/case/step/trace/action objects use the corresponding SDK constructor
property names in camelCase; nullable scenario/action/value fields are explicit.
No envelope embeds its own revision. UTC metadata has zero offset.

Every operation validates the existing root, all records and archives under its
one lease. It rejects links/reparse points, nonregular files, unknown entries,
unexpected or incomplete records, unsupported schemas, wrong project identity, invalid UTF-8/Unicode,
unknown/duplicate/missing fields, invalid enums, malformed IDs/hashes and broken
trace references. Refusal preserves existing bytes. Publication uses exclusively
owned adjacent staging, flush and atomic rename; cleanup removes only staging
owned by that operation. These are cooperating-store defenses, not a sandbox or
a guarantee against malicious trusted code, every TOCTOU race or power loss.

| Boundary | Limit |
| --- | --- |
| Original source bytes / encoded source envelope | 1 MiB / 4 MiB |
| Sources per project / items per source / criteria per item | 64 / 256 / 16 |
| Title, document ID / locator / provider ID, version | 200 / 512 / 100, 40 UTF-16 units |
| Description, criterion, prerequisites, instruction, expected | 4096 UTF-16 units |
| Case sets / cases per set / steps per case / traces per case or step | 128 / 200 / 20 / 16 |
| Encoded case envelope / immutable run-bound archives | 4 MiB / 256 |

Required text/collections are nonempty; required text cannot be whitespace-only.
Description and prerequisites may be empty. Valid astral Unicode, tabs and line
breaks are preserved. Limits are checked before publication and again on load.
The executable-run limit is 200 total steps; a stored case set can contain
more. Maximum-size native UI performance has not been measured.

Core captures actual deep copies of bytes and collection elements synchronously
before scheduling IO, and returns detached snapshots. The facade first retains
actual operation admission, then calls the reusable `TestValidation.Copy*`
helpers outside Core/coordinator locks before returning. The store independently
captures those host-owned copies before its own IO.
No plugin delegate or caller collection is enumerated under the store lease.

Failures use sealed `PluginTestingException` with a fixed generic message, safe
`ErrorCode` and no raw inner exception. Allowed codes are TestingContextClosed,
InvalidTestSource, InvalidCaseSet, InvalidRun, InvalidEvidence, RevisionConflict,
StoreLimitExceeded, StoreIoFailure, UnsafePath, TestStoreCorrupt, RecoveryRequired,
ManualStepUnsupported, TargetUnavailable, AssertionFailed, AdapterFailed,
TestRecordNotFound, StoreBusy, InvalidId, InvalidProducer and TestingFailed.
The facade maps unknown public exception codes to TestingFailed and never
displays raw exception/provider text. Native source/case text is separate from
diagnostics.

The SDK snapshot contains eleven allow-listed source files, two 0.4.0 NuGets,
both consumer READMEs, source/package hashes and `testingServices=1`.
`workbench-sdk-0.4.0` requires Windows packaging for the actual WinUI PRI.
Linux `-ReferenceOnly` and native reference compilation establish no native
runtime, installed-plugin, picker, Windows 11, IME, DPI or accessibility result.

## Durable plans, publication and ownership

`CreateRunAsync(owner,setId,expectedCaseRevision,selectedCaseIds,ct)` copies the
selection synchronously before scheduling. Selection is nonempty, unique and in
the frozen set's order; every selected step must have an allowed typed action,
with at most 200 total steps. It fixes `virtual.task-form.v1`. Under the SAME
outer exclusive `StoreLease`, it checks the exact current revision, validates
selection, awaits `FreezeCaseRevisionLockedAsync`, then publishes the new plan
and Queued event together. It never calls a public lease-acquiring helper from
inside that lease and never falls back to an older archive on a stale revision.

A shared case set may author a NEW run owned by the executing plugin. The
source, case-set and archive producer/trace identities stay unchanged. This is
the controller's shared-read/own-write interpretation, not a per-record sharing
policy approved by an end user. Append, evidence creation and retry require the
run's captured owner. Shared reads remain available inside the captured project.

`CreateRetryRunAsync(owner,parentRunId,ct)` permits only an owned Failed,
Cancelled or Interrupted parent. It creates a new run ID and session, links the
immediate parent and copies the parent's exact archived cases and selection,
without consulting an edited current set. Completed/nonterminal/foreign parents
refuse. Every ancestor must exist, preserve owner/set/revision/selection, have an
eligible terminal result, and be acyclic; session IDs are unique in the store.
Retry starts again from step 1 and makes no checkpoint or exactly-once claim.

Schema1's `runs` directory is optional for existing source/case-only stores and
is created by the first run write; it is fully validated when present. A new
run is assembled in `runs/<newRunId>.stage/` with plan.json, head.json, events/
and evidence/; the plan, sequence-0 Queued event and its matching head are flushed before one
same-root directory rename publishes `<newRunId>/`. There is no runnable plan
without its matching genesis and authoritative head. Complete unreferenced case archives are retained
and counted (256 total), including when a later run publication fails.

PlanEnvelope has these exact required fields:
`schemaVersion,id,sessionId,projectId,producerPluginId,createdUtc,parentRunId,caseSetId,caseRevision,cases,targetId`.
Its private encoded limit is **4 MiB + 4096 bytes**, reserving bounded host
metadata beyond a valid 4 MiB case envelope. The source/case limit remains 4 MiB
and the event limit remains 256 KiB; no caller can tune these limits. Read and
write use the same plan limit. A canonical 4 MiB selected-case fixture exercises
real creation, exact archive bytes, reopen and retry with plan bytes above 4 MiB;
this does not rely on padding removed by plan serialization.
`parentRunId` is explicitly null for an original run. PlanHash is SHA256 of the
exact plan.json bytes, not a field inside that file. Frozen plan cases must
exactly equal the selected archived cases, including IDs/order/text/actions and
traces; the referenced archive must exist even while the same revision is current.

## Event wire and fixed replay policy

Each immutable `events/<sequence D10>.json` has exact required fields:
`schemaVersion,runId,sequence,priorHash,planHash,createdUtc,kind,caseId,stepId,outcome,errorCode,evidenceIds,nativePlainTextState,isRecovery,hash`.
Optional values are explicit null; evidenceIds is an explicit array, empty on
non-result events. `isRecovery` is a required private Boolean, absent from the
public SDK draft. It is false on genesis and every caller event, and true only
on an internal initialization Interrupted event with ErrorCode RecoveryRequired.

Genesis sequence is **0**, priorHash is 64 ASCII zeroes and kind is host-only
Queued. An event's hash is SHA256 of its **exact UTF-8 file bytes after replacing
only the hash value's 64 literal ASCII digits with 64 ASCII zeroes**. The stored
hash is then substituted into that fixed-width slot. This avoids a circular
self-hash, includes every other raw byte (including whitespace, escaping,
nativePlainTextState and isRecovery), and detects an edited final event even
without a successor. Hash values are literal 64-character lower hex, never
JSON-escaped. The next event's priorHash must equal that stored verified hash;
every planHash must equal the exact current plan-file hash. These hashes detect
corruption; trusted code that rewrites a complete chain and anchor is outside this
cooperating-store integrity boundary. Raw-file SHA256 artifact receipts remain
separate from the event's masked-slot digest.

Each published run requires `head.json`, a strict schema1 object with exactly
`schemaVersion,runId,planHash,sequence,eventHash`, at most **4096 encoded bytes**.
The run/plan identities must match the already-validated plan; sequence is 0..511,
and eventHash is the committed event's masked-slot digest. Generation0 commits
Queued sequence0. A missing/malformed/future/wrong-identity head refuses the
published run and whole store; no legacy run is adopted as a new sequence0.

Under the SAME existing StoreLease, append first validates the complete anchored
journal and caller CAS, durably writes the next immutable event, then atomically
replaces head.json with the matching new commitment. Success and lease-cache
advance occur only after both publications. If head publication fails, the new
event and old head remain; no immutable event is rolled back or deleted. A
process interruption can likewise leave a head temporary, head behind/ahead,
missing committed suffix or extra unanchored tail. These states preserve/refuse;
they are never guessed, advanced, shortened or replaced by recovery history.

Replay reads **all** contiguous sequence files from 0, verifies each self-hash,
prior link, plan binding, host UTC, field shape, Unicode, receipts and transition,
then requires the final sequence/digest to exactly equal head.json. Surviving
files alone do not establish the authoritative head or length. A missing suffix
therefore cannot become an older valid prefix or replacement Interrupted event.
File timestamps never select the head. A missing/gapped/duplicate/unknown file,
future schema/field, malformed event or false Completed refuses the entire store
and preserves its bytes; no older head, empty view or historical success fallback.
Append validates expectedSequence **and** expectedHeadHash under the outer lease.

Malformed/missing/wrong-identity anchors return TestStoreCorrupt; oversized heads
return StoreLimitExceeded. A structurally valid chain whose sequence/digest does
not match its head, or a recognized `.head.json.<managedId>.tmp`, returns
RecoveryRequired. Interior gaps and malformed events remain corrupt. Links stay
unsafe. Every read/append and ANY owner initialization validates these conditions
before returning data or writing recovery, including anchors on foreign runs.

| Current journal | Allowed next events |
| --- | --- |
| Queued | RunStarted, CancelRequested, Failed, Cancelled, Interrupted |
| Running, no failed result | Next ordered StepStarted, matching StepFinished, CancelRequested, Failed, Cancelled, Interrupted; Completed only after every planned step passes |
| CancelRequested, no failed result | Matching already-started StepFinished, Failed, Cancelled, Interrupted |
| Failed StepFinished awaiting settlement | Caller Failed only; internal owner-initialization recovery Interrupted |
| Any terminal state | None |

RunStarted occurs once. Only one step may be in flight. StepStarted has exactly
the next frozen case+step IDs and is committed before the future runner acts.
StepFinished requires that same started step, a defined outcome, 1–256 unique
owned durable receipt IDs and exact observed native state. A passed result has
no error code; failed/terminal errors may be absent or from the safe allow-list.
Unknown caller error strings map to TestingFailed before storage; unknown stored
error strings refuse replay. Caller collection exceptions are also translated
through that same allow-list before returning a faulted task.

CancelRequested is nonterminal. No RunStarted/new StepStarted/Completed is allowed
after it; only the already-started action can finish, then terminal settlement.
Failed, Cancelled and Interrupted may honestly close an unfinished uncertain
step without manufacturing a result or native text. Cancellation does not prove
that no action occurred and does not imply rollback.

## Passive evidence, retained orphans and interrupted staging

`AddRunEvidenceAsync(owner,runId,draft,ct)` synchronously copies bytes, accepts
only image/bmp plus IsSimulated=true, and requires an owned nonterminal run.
It writes and flushes frame.bmp and receipt.json in
`evidence/<newEvidenceId>.stage/`, then publishes both with one same-root rename
before returning the host receipt. A referenced StepFinished can therefore be
committed only after its receipt is durable. Complete receipts not yet referenced
by any event are valid retained orphans; they count against all evidence limits.

ReceiptEnvelope has exact fields:
`schemaVersion,id,runId,projectId,producerPluginId,mimeType,length,sha256,isSimulated`.
SHA256 covers exact frame bytes. Reads and replay validate every receipt, actual
length/hash/format and run/project/owner binding, including unreferenced receipts.
Bytes returned by ReadRunEvidence are detached copies. There are no caller paths.

The BMP is exactly 172854 bytes: BM, file size 172854, both reserved words zero,
pixel offset 54, BITMAPINFOHEADER size 40, width 320, **positive** height 180,
planes 1, depth 24, compression 0, image size 172800, both pixels-per-meter fields
zero, color-used/important fields zero, then bottom-up BGR pixels. Core validates
this passive layout and the simulated flag, not OCR or watermark pixels. The
later renderer must visibly draw SIMULATED and never capture a real desktop.

Limits are 256 runs/project, 512 events/run, 256 KiB per encoded event, 512 KiB
incoming evidence, 256 receipts/run and 128 MiB actual evidence/run. With today's
only valid 172854-byte frame, 256 receipts total **44,250,624 bytes**, below 128 MiB;
the aggregate byte guard is conservative and has no reachable valid boundary
under this format/count combination. Likewise the fixed 200-step state machine
reaches at most 404 events; the 512-event guard also rejects malformed excess
files. No artificial valid maximum, tunable test limit or automatic pruning is
claimed. Tests use real complete independent orphan fixtures to exercise counts.

Recognized generated `.stage` directories (complete or partial), or generated
`.D10.json.<managedId>.tmp` event files or `.head.json.<managedId>.tmp` head files,
are preserved and cause RecoveryRequired;
they are never treated as runnable/empty data. Unknown entries still refuse as
corrupt, and links/nonregular paths refuse as unsafe. Partial **published**
records are malformed and preserved/refused. Only caught ordinary failure or
cancellation may clean that actual call's generated stage, after regular-file,
link and expected-entry checks. Unexpected/foreign/stale entries are never
cleaned. Repair of interrupted partial staging is an explicit manual boundary;
there is no public reset/recover API, all-crash recovery or power-loss guarantee.

## Owner initialization and remaining integration gates

Internal Core-only `InitializeOwnerAsync(producerPluginId,ct)` implements the
facade's retained once-per-new-context barrier. Missing-store initialization
creates nothing. It first validates the **whole** existing store under one
lease; every required head and full chain must agree before the first recovery
write. Any corrupt/staged/unsupported/unanchored record prevents all recovery writes. It
then appends host-only isRecovery=true Interrupted to only that owner's valid
nonterminal old runs, preserving all previous event/text/evidence bytes. Terminal
and foreign-owner runs are untouched. A second initialization adds nothing to
the already interrupted records. Cancellation between recovery writes can leave
a validated partially settled group; no multi-run transaction is claimed.
Recovery appends use the same event-then-head publication protocol. There is no
fallback to an older head/backup or automatic repair of partial publication, and
no all-crash, rollback or power-loss transaction guarantee.

The narrow recovery exception after a failed StepFinished is a controller ruling:
normal callers still must settle Failed; host initialization records interruption
without rewriting the failed observed result. The hashed flag cannot be set by
a caller, is invalid on any other event kind and is invalid after a terminal event.

The real `PluginRegistrationContext` implements optional `IPluginTestingContext`.
Its fixed facade captures the scope and plugin ID before Initialize, so plugins
may await testing reads before contributions commit. Acquiring the context or
service property starts no store IO. The first actual call publishes both its
retained operation and a gated initialization task before any input getter or
filesystem work. Every call joins that same memoized barrier; failure remains
visible for that context, and a corrected new context may reattempt.

Initialization uses `CancellationToken.None` as a context-owned policy. A caller's
cancellation does not cancel or detach it. Each operation joins **both** actual
input capture and initialization, even if either fails first; it then checks the
caller token before invoking its store action. Thus cancellation may wait for
the bounded store lease attempt. Cancellation after store admission still does
not promise rollback. Closed fresh calls return TestingContextClosed before
touching caller collections or starting initialization.

Testing calls and initialization participate in HasPendingProjectWork, normal
preparation drain and terminal context drain. Normal before-leave hooks execute
while services remain open, then join admitted calls. Terminal Close immediately
revokes fresh service calls, including from plugin Stop, and retains admitted
work until it actually settles before the manager/context/root may retire.
Low-level Disable/Shutdown do not rerun preparation hooks. Unexpected close may
leave a durable nonterminal run for next-generation owner initialization.

Portable tests exercise the real captured facade, package-loaded fixture,
manager/HostRuntime/root leases, held caller prefixes, filesystem initialization
gates, defensive copies and safe lifecycle errors. The portable SDK-only `Workbench.Testing.Domain` now implements and consumes the local providers, rule generator, CSV exporter, coalesced CAS editor, typed virtual adapter, passive renderer and supervised controller. Task4 tests compose the real Core registration/facade/supervisor before Commit and use actual filesystem gates and write obstructions. The entry-facing controller and immutable models live under `plugins/Workbench.Testing.Domain`. The independent native entry, package allow-list/source/ZIP auditing, package-store install probe, and actual shipping-EXE seed/verify probes are now implemented. Current-source actual WinUI/PRI/process execution and final downloaded-byte auditing remain pending the controller's Windows workflow gate; Linux reference compilation and synthetic report regressions do not establish those results. Existing native,
management/theme/restart/workspace/business/process checks remain required;
portable Core tests do not substitute for them. Windows Server automation stays
separate from deferred Windows 11 human/picker/IME/DPI/accessibility/device checks.

### Monotonic mapping removal

Case updates may remove an existing action to manual even when instruction and expected text equal the stored text. This preserves permanent invalidation when a changed or invalid edit is restored before autosave. Metadata-only editor operations still preserve the binding. A non-null action may never be introduced or substituted by an update, and changed step text still requires null. Owner, revision CAS, IDs, order and exact traces remain enforced.

### Portable CSV trace rows

The domain exporter writes UTF8 with BOM, CRLF records, quoted fields and doubled internal quotes. Each step emits the complete stored case-trace list, followed by its stored step-trace list, preserving each list's order and duplicates. `traceScope` is `case` or `step`; `traceOrdinal` is one-based within that scope. Repeating case references for another step is deliberate. `caseId`/`stepId`/`traceScope`/`traceOrdinal` identify each trace entry. Every text data cell receives one apostrophe, including IDs, revisions, locators, scope and empty text. Only the validated numeric `stepOrdinal`, zero-based `criterionIndex` and positive `traceOrdinal` omit it. This changes exported cell values for spreadsheet viewing; stored text and trace arrays remain exact. It does not promise safety after arbitrary spreadsheet resaves.
