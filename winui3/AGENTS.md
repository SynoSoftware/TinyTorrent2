# WinUI and Synapse

Follow the [root instructions](../AGENTS.md). This directory contains the reusable
Synapse library, sample, and control tests. The new product interface follows the
[architecture](../docs/architecture.md); it is not the old Transmission client.

## Read for the change

- Before designing or changing a product surface, read
  [interface guidance](../docs/interface.md).
- Before changing TableView behavior or its public API, read the relevant sections
  of the [contract](docs/tableview-contract.md), using the
  [table vocabulary](CONTEXT.md).
- Before changing table state ownership, input, rendering, or layout, read the
  [implementation map](docs/tableview-implementation.md).
- Text and formatting follow [localisation](../docs/localisation.md), including
  generated control resources and live language changes.
- Test scope follows [testing](../docs/testing.md). Building a WinUI test host
  does not run it; running it opens a real window and requires an explicit request.

## Control work

Preserve the public and observable contract. A simpler mechanism may replace an
existing one, but a perceived flaw in the contract is a finding to resolve before
changing behavior. Do not change the specification merely to match a regression.

Keep the control independent of torrent types, commands, persistence, and product
preferences. The host owns domain policy; Synapse owns the mechanics described by
its contract. Use the existing non-torrent sample to check that new control
behavior belongs in the library, rather than inventing another application host.

Inspect the current code before acting on historical findings. In particular,
archived construction notes are not a second implementation plan. Keep shared
geometry, selection, source projection, and gesture state at the owners named in
the implementation map. A menu action and its gesture call the same operation.

Acknowledge input promptly while expensive work proceeds. Preserve the deliberate
press/release decisions in contract sections 14 and 16; the time a person holds
an input is not evidence of a slow handler.

Use the platform value when Windows owns a threshold, theme metric, or behavior;
use resolved geometry for geometric values and the contract for specified
baselines. Verify resource names against the installed platform rather than
inventing them. A local template default is not a named design token; the table's
resource constraints and reasons live in its contract.

Search all string-bound consumers when changing template parts, visual states,
resource keys, or automation identifiers. Typed reference searches alone cannot
find those contracts. Keep live XAML bindings live; `x:Bind` defaults to one-time.

## Build and evidence

Project files and [Synapse.slnx](Synapse.slnx) define the supported targets and
package versions. Use Visual Studio MSBuild for the affected WinUI target. There
is no product-engine build or release stager yet.

Keep [Directory.Build.props](Directory.Build.props)'s generated-output exclusions:
they prevent the XAML compiler's `bin-fl` output from re-entering source globs.
If changing them, verify repeated builds do not deepen the output tree.

Do not launch the sample, UI test host, or product unless explicitly requested.
If rendered or out-of-process automation evidence is needed but unavailable,
report that gap. A compile result, an in-process automation cache, or an old
measurement cannot establish current pixels, Narrator behavior, or responsiveness.
