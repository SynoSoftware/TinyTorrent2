# TinyTorrent2

Write elegant, obvious code: the smallest arrangement of parts that makes the
behavior clear. Each concept has one owner, each operation one implementation,
and each part one responsibility. Choose plain code that a reader understands
on the first pass.

For product and implementation decisions, apply
[Usability comes first](docs/architecture.md#usability-comes-first).

## Read for the change

- Before changing ownership, dependencies, or project structure, read
  [architecture](docs/architecture.md).
- Before changing engine state, persistence, activation, or shutdown, read the
  [engine contract](docs/engine.md).
- Before changing communication, read the [protocol contract](docs/protocol.md).
- Before changing text, language selection, or display formatting, read
  [localisation](docs/localisation.md).
- Before changing WinUI or its tooling, read [winui3/AGENTS.md](winui3/AGENTS.md).
- Before writing, changing, deleting, choosing, or running tests, read
  [testing](docs/testing.md). It decides whether a test is worth writing.
- Use [product vocabulary](CONTEXT.md) and, for controls,
  [table vocabulary](winui3/CONTEXT.md).

The [documentation guide](docs/README.md) identifies current authorities and
historical references. The source repository at `../TinyTorrent` stays untouched.
Transmission, TypeScript concepts, browser architecture, and web state models
do not define this product.

## Working rules

- State assumptions and verifiable success criteria before implementing. Resolve
  material ambiguity before dependent changes.
- Inspect the existing owner, data flow, and adjacent patterns before editing.
  Commands and gestures for the same operation call the same implementation.
- Add a type or abstraction only when it owns a concrete decision or hides
  necessary complexity. Separate responsibilities do not require separate
  projects, interfaces, or processes.
- Touch only what the request requires. Match the existing style and remove only
  orphans created by the change. Keep unrelated cleanup out of the diff.
- Prefer explicit state and legal transitions over combinations of flags. Keep
  computed display values as projections, rather than another mutable authority.
- Let names and structure carry intent. Comments explain constraints, evidence,
  and reasons the code cannot express; update them with the code they explain.
- Read the final diff. Every new file, type, state, dependency, and branch must
  answer a concrete requirement.
- Build affected native targets when compilation evidence is needed. Launch
  applications and desktop test hosts only when explicitly requested. Close
  what you launched and report what was verified and any material gaps.

## Naming

Use one canonical term for each concept. Prefer established domain and platform
vocabulary. Judge clarity where a name is read: the owner, namespace, and type
carry context; the name carries the distinctions that remain.

Name types and state with nouns, operations with verbs, booleans as affirmative
conditions, and collections with plurals. Challenge filler and repeated context,
but keep every word needed for meaning. There is no word-count limit and no rule
that requires splitting a coherent type to shorten a name.

Files and folders follow the same principle. Use familiar language conventions,
including partial-type filenames where they help navigation. Renaming a public
member changes a contract; review its callers and specification together. Search
literal consumers of resource keys, template parts, and visual states as well as
typed references.
