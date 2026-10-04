# Architecture skill audit — 2026-10-03

This is a dated evidence record, not another contract or implementation backlog.
The [architecture](../architecture.md) and its [live decisions](../architecture.md#decisions-still-open)
remain authoritative. The engine and product UI are not implemented.

## Coverage

Screened the full available skill catalogue and applied these workflows:

- `codebase-design` and `domain-modeling`: module boundaries, ownership, and
  canonical vocabulary.
- `research`: independent engine, Windows, and WinUI investigations against
  pinned libtorrent 2.1.2 source and Microsoft documentation, cross-checked with
  the other architecture chat and this checkout.
- `karpathy-guidelines` and `writing-for-agents`: minimal corrections and one
  document authority for each rule.
- `winui-app`, `winui-code-review`, `winui-my-design`, and `winui-packaging`:
  retained control source, host integration, native input/accessibility, and
  process and release lifetime. Deployment instructions were used for review;
  no installation or signing was performed.

The commit-based `code-review` workflow could not run as designed without a Git
baseline. Current source was checked against its contracts instead. Generic
prescriptions to add Toolkit, force all bindings to `x:Bind`, or clear and
repopulate collections conflict with this repository and were not applied.
Interview, prototype, build, desktop-execution, and toolchain-setup workflows
were unnecessary for this review. Web, cloud, database, marketing, and artifact
skills did not address the task. No skills were installed or made mandatory for
future changes.

## Findings and disposition

| Finding | Disposition and evidence |
| --- | --- |
| Even critical libtorrent completion alerts can be dropped. | [Engine work](../engine.md#state-and-work) now covers lost completions and borrowed alert-data lifetime, citing the pinned queue and session source. |
| A file-choice edit can complete asynchronously or partly fail; an immediate checkpoint can still contain old choices. | [Committed edits](../engine.md#committed-edits) now distinguishes accepted, effective, and saved facts while work is pending, preserving ordered edits without a revision framework. |
| Enabling payload before membership commits can leave unrecorded files. Supplied piece priorities can override a preview's zero file priorities. | [Addition](../engine.md#addition-and-identity) now orders commit before payload and controls preview parameters. The latter follows pinned initialization; the former is a failure case derived from the required recovery behavior. |
| Pipe loss does not establish UI process exit. Foreground activation and replacing a resident engine need Windows lifecycle handling. | [Activation](../engine.md#startup-and-activation), [shutdown](../engine.md#closing-and-shutdown), and [persistence](../engine.md#persistence-and-file-safety) now address process ownership, foreground permission, update handover, and newer-store refusal, with Microsoft references. |
| Periodic source replacement cancels table dragging. A late detail reply can populate a different inspector context. | [Presentation flow](../architecture.md#command-and-presentation-flow) and [detail delivery](../protocol.md#snapshots-and-detail) now state the host rules. These are planned-integration failure cases, not observed product defects. |
| Retained Synapse leaves source subscriptions active after unload, undoes native automation selection, and lacks peers for custom header/status exposure. | Recorded with exact source owners in [known integration gaps](../../lib/TableView/docs/tableview-implementation.md#other-known-integration-gaps). These remain implementation work. |
| Suppressed row focus conflicts with the visible-keyboard-location contract; header arrow handling assumes LTR order. | Recorded in the same [control map](../../lib/TableView/docs/tableview-implementation.md#other-known-integration-gaps). The rejected historical focus ring was not restored. RTL consequences follow source plus platform mirroring and still need live verification. |

## Conclusion and limits

Retain the engine, WinUI, and Synapse responsibilities. None of these findings
justifies another process, scheduler, state store, transport, or table
implementation. They do show why a sound module boundary is not proof that its
implementation meets the contract.

The pass updated documentation only. No application code, build, desktop test,
rendering check, or libtorrent experiment was part of it. In particular, payload
guards, lost-completion recovery, Narrator/UI Automation, and live RTL behavior
remain unverified at runtime. Apply the existing [testing policy](../testing.md)
when the corresponding implementation changes; do not add a broad suite merely
because this audit listed several findings.
