# TinyTorrent2

Write elegant, obvious code: the smallest arrangement of parts that makes the
behavior clear. Each concept has one owner, each operation one implementation,
and each part one responsibility. Choose plain code that a reader understands
on the first pass.

For product and implementation decisions, apply
[Usability comes first](docs/architecture.md#usability-comes-first).

## Every rule states why

A rule in these instructions or in a contract states what it protects, in the
present tense. Without its reason, nobody can tell whether the rule still
applies, so it is either copied into cases it was never about or dropped when it
is inconvenient. How a rule arose belongs in the commit message.

Before applying a rule, check that its reason holds in the case in front of you.
If it does not, report that and ask before continuing; do not comply anyway and
do not quietly ignore it. A rule whose reason you cannot find is a finding.

## Plans start from common sense

A plan or contract passes these tests, because each mechanism it asks for is
permanent design, code, and review cost that the user never sees.

- Start from what established libtorrent clients such as qBittorrent and other
  Windows desktop applications already do. A different design names the user
  benefit it gives; without one, follow the established design.
- A requirement names the failure a user would see without it. A rare edge case
  gets one sentence of reasoning, not a mechanism, because most edge cases never
  occur and every mechanism must still be built and maintained.
- List a decision as open only when two reasonable answers exist; otherwise
  state the default at its owner. A list of open decisions makes obvious
  defaults look uncertain and delays the work that depends on them.
- A review also removes what no longer passes these tests. Reviews that only add
  turn each edge case they find into a requirement, which is how a plan loses
  common sense.

## Read for the change

- Before changing ownership, dependencies, or project structure, read
  [architecture](docs/architecture.md).
- Before changing engine state, persistence, activation, or shutdown, read the
  [engine contract](docs/engine.md).
- Before changing communication, read the [protocol contract](docs/protocol.md).
- Before changing text, language selection, or display formatting, read
  [localisation](docs/localisation.md).
- Before changing the product UI, read [WinUI instructions](app/AGENTS.md).
- Before changing the table library, its development hosts, or its tooling, read
  [TableView instructions](lib/TableView/AGENTS.md).
- Before writing, changing, deleting, choosing, or running tests, read
  [testing](docs/testing.md). It decides whether a test is worth writing.
- Use [product vocabulary](CONTEXT.md) and, for controls,
  [table vocabulary](lib/TableView/CONTEXT.md).

The [documentation guide](docs/README.md) identifies current authorities and
historical references. The source repository at `../TinyTorrent` stays untouched.
Transmission, TypeScript concepts, browser architecture, and web state models
do not define this product.

## Working rules

- State assumptions and verifiable success criteria before implementing. Resolve
  material ambiguity before dependent changes.
- Inspect the existing owner, data flow, and adjacent patterns before editing.
  Commands and gestures for the same operation call the same implementation.
- Follow the existing solution to the same problem. Departing from it needs a
  requirement you can name; "cleaner" and "more flexible" are not requirements.
  When the existing solution is the worse one, report it instead of building a
  third. Usability wins when it conflicts with consistency.
- When the change exposes two implementations of one rule, move their direct
  callers to the established owner in the same change; two matching copies are
  free to drift. Report duplication outside the change instead of widening it.
- Add a type or abstraction only when it owns a concrete decision or hides
  necessary complexity. Separate responsibilities do not require separate
  projects, interfaces, or processes.
- Touch only what the request requires. Match the existing style and remove only
  orphans created by the change. Keep unrelated cleanup out of the diff.
- Prefer explicit state and legal transitions over combinations of flags.
- Store facts and decisions; derive display values and other computed state
  when read. A stored derived value is a second answer that can drift, so keep
  one only for a measured reason and name it as derived.
- State an invariant in a type wherever the type system can express it: a
  comment asks every later writer to remember, while a type refuses for them.
  One owner enforces an invariant that a type cannot express.
- Let names and structure carry intent. Prefer straight-line control flow and
  early returns where they clarify a decision; a reader follows one operation
  without crossing pass-through layers. In C#, use the null-forgiving `!` only
  where a real invariant guarantees the value; elsewhere it hides the null it
  claims cannot happen.
- Read the final diff. Every new file, type, state, dependency, and branch must
  answer a concrete requirement.
- Never run `engine\src\Dependencies.ps1`, and never delete, move, rename or
  rebuild anything in `3rdParty/`. The owner compiles the dependencies once,
  because building them again takes over an hour of the owner's machine. When
  a build reports that `3rdParty/` is incomplete, stop and report it.
- Build affected native targets when compilation evidence is needed. Make all
  the edits first and build once, because each build holds the owner's
  machine. Launch applications and desktop test hosts only when explicitly
  requested. Close what you launched and report what was verified and any
  material gaps.
- After every build, compare the files it compiled, which the build log lists,
  with what you changed. A source edit recompiles that file; a header edit
  recompiles the files that include it. When a build compiles more than that,
  or takes longer than its file count explains, find the cause and fix the
  build before building again, because every later build pays the same cost.
- After every build or test run, check that it did not start a recursive copy.
  All generated output belongs in `artifacts/`, so this command, run from the
  repository root, must print nothing:
  `& "C:\Program Files\Everything\es.exe" -path $PWD /ad "wfn:bin|wfn:obj|wfn:bin-fl|wfn:TestResults" "!*\artifacts\*" "!*\3rdParty\*"`.
  The one exception is `3rdParty/`, the compiled dependencies, which stays
  outside `artifacts/` so cleaning build output never forces a dependency
  rebuild ([third-party dependencies](docs/architecture.md#third-party-dependencies)).
  The check skips it because its checkouts and tools contain their own `bin`
  folders, which this repository's builds do not write.
  A result is a defect: report it and find the output path that caused it
  before continuing. Do not delete the folder and move on.

## Naming

Read [naming and structure](docs/naming.md) before naming or placing a type,
member, file, folder, namespace, or resource key, or before adding an interface
or a signature, and run its naming review on every new or changed name. One
policy serves every project.

Each project or library has its own `Enums.cs` beside its project file, and
every enum that project declares lives there, one member per line, including
enums only one type uses. This gives each project's vocabulary one discoverable
home instead of scattering declarations among their current consumers.

## Code comments

Read [code comments](docs/comments.md) before adding, changing, or reviewing
comments or API documentation. Comments are short and written only when the
code cannot carry the knowledge itself. Before reporting a code change complete,
check every comment added in the diff against that policy and remove the ones
that fail. This catches explanatory prose that looked useful while writing but
adds nothing once the code is in place. One policy serves every project.
