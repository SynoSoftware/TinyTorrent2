# TinyTorrent2

## First directive: common sense above all

**Owner ruling.** Use judgment to achieve the user's actual goal with the least
unnecessary effort. Every repository rule and plan serves that outcome; apply
it by its purpose, not mechanically. Resolve routine decisions within the
authorization already given. Ask when an unresolved choice materially changes
scope, risk, or authorization. Match implementation, review and validation effort
to the change, because process that costs more than it protects delays delivery.

For product and implementation decisions, apply
[Usability comes first](docs/contracts/architecture.md#usability-comes-first).

## Every rule states why

A rule in these instructions or in a contract states what it protects, in the
present tense. Without its reason, nobody can tell whether the rule still
applies, so it is either copied into cases it was never about or dropped when it
is inconvenient. How a rule arose belongs in the commit message.

Before applying a rule, check that its reason holds in the case in front of you.
If it does not, apply the first directive and explain the departure. A rule whose
reason you cannot find is a finding.

A rule marked **Owner ruling** is the owner's settled decision. Reviews, plans
and later agents build on it as fixed. When a case seems to conflict with one,
follow the ruling and report the conflict to the owner, because a ruling that
each new agent reopens changes the product's direction from session to session.

## Plans start from common sense

A plan or contract passes these tests, because each mechanism it asks for is
permanent design, code, and review cost that the user never sees.

- Start from what established Windows desktop applications already do. A
  different design names the user benefit it gives; without one, follow the
  established design.
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
  [architecture](docs/contracts/architecture.md).
- Before changing engine state, persistence, activation, or shutdown, read the
  [engine contract](docs/contracts/engine.md).
- Before changing communication, read the [pipe API contract](docs/contracts/pipe-api.md).
- Before changing the Library database schema, read the
  [database contract](docs/contracts/database.md). Every change from the current
  revision 9 on is a migration that keeps saved data, because editing the
  schema and raising its version makes saved Library databases unusable.
- Before changing text or translations, language selection, or display
  formatting, read [localisation](docs/contracts/localisation.md).
- Before changing the product UI, read [WinUI instructions](app/AGENTS.md).
- Before changing the engine's popup, its buttons or another engine-drawn
  control, read [engine instructions](engine/AGENTS.md).
- Before changing the table library, its development hosts, or its tooling, read
  [TableView instructions](lib/TableView/AGENTS.md).
- Before writing, changing, deleting, choosing, or running tests, read
  [testing](docs/agents/testing.md). It decides whether a test is worth writing.
- Before planning work that other agents will do, briefing them, or checking
  their results, read [coordination](docs/agents/coordination.md).
- Before designing or changing a type, an enum, an interface, or where a seam
  goes, use the `/codebase-design` skill. It gives every session the same design
  vocabulary and principles, so a design is judged by one standard instead of
  each agent's own.
- Use [product vocabulary](CONTEXT.md) and, for controls,
  [table vocabulary](lib/TableView/CONTEXT.md).

`docs/contracts/` describes how the product works now and is the authority for
it. `docs/agents/` holds the working rules linked here. `docs/plans/` holds only
work that is open or in progress; a finished plan moves its rules into the
contracts and is deleted, because a finished plan read as current misleads. The
[documentation guide](docs/README.md) lists every document. The source
repository at `../TinyTorrent` stays untouched.
Transmission, TypeScript concepts, browser architecture, and web state models
do not define this product.

## Working rules

- **Owner ruling: filename discovery.** Use only
  `C:\Program Files\gozler\gozler.exe`, with the narrowest known `-path`, a
  specific name/pattern and `-n 20`, so broad results do not flood agent
  context. If it exits 10 because `-n` cut the list off, refine the query
  rather than treating the list as complete. Never run `es.exe` or `goz.exe`.
  If gozler cannot answer after a refined query, report that. For a sandbox pipe denial, retry
  the same read-only command outside the sandbox before treating the tool as
  unavailable. Use `rg` for file contents.
- **Owner ruling: user steers are cumulative.** Keep earlier requirements and
  unfinished work in scope unless the user explicitly cancels or replaces them,
  and execute them in the order received. A later steer does not outrank an
  earlier one merely because it is newer, so follow-ups cannot silently drop or
  reorder the work.
- **Owner ruling: update only `en.json` during normal work.** Refresh all other
  language catalogues from English at release, or for an explicit translation
  request, because translating unsettled text for every patch wastes work.
  [Localisation](docs/contracts/localisation.md) owns catalogue structure and fallback.
- State assumptions and verifiable success criteria before implementing. Resolve
  material ambiguity before dependent changes.
- Inspect the existing owner, data flow, and adjacent patterns before editing.
- Before declaring implementation complete, perform the
  [focused review passes](docs/agents/compilation.md#focused-review-passes). They trace
  behavior, simplify ownership and recheck corrections, because one broad review
  can miss defects and the fixes can introduce others.
- **Owner ruling: compile last.** Before compiling, launching a built
  executable, or handling a build failure, read
  [compilation](docs/agents/compilation.md). Each build costs minutes of the owner's
  15 W laptop, which agents share, so a task compiles only when nothing else
  can answer its remaining questions, and then once for all of them.
- **Owner ruling: finish the requested work, including its validation.** An
  implementation, fix or review authorizes the focused tests and application
  inspection needed to verify that work. Use shared build outputs and choose
  checks under [testing](docs/agents/testing.md), including its separately restricted
  checks, because compiling without using the result can leave the actual
  behavior unverified. Respect explicit user restrictions and protect the
  owner's data and running applications. Close what you launched and report
  the evidence and any remaining gaps.
- **Owner ruling: end a TinyTorrent that blocks you, unless it is capturing.**
  A capture run belongs to another session taking screenshots, and ending it
  breaks that run, so leave it and wait for it. A capture run starts from a
  `_capture` output folder, such as
  `artifacts\bin\TinyTorrent\debug_win-x64_capture`. Any other TinyTorrent
  engine or window that blocks a build, check or test is usually a daemon
  someone forgot open. End it with `Stop-Process` and continue, because a
  session that stops to report it blocked delivers nothing.
- Do not report or change line endings on disk. Files there mix CRLF and LF
  because Windows editors and tools write CRLF, and `.gitattributes` converts
  every text file to LF on commit, so the mix never reaches a diff or history.
  A line-ending change that does show in `git diff` is a defect in that
  conversion: report it.
- Never run `engine\src\Dependencies.ps1`, and never delete, move, rename or
  rebuild anything in `3rdParty/`. The owner compiles the dependencies once,
  because building them again takes over an hour of the owner's machine. When
  a build reports that `3rdParty/` is incomplete, stop and report it.
  `3rdParty/goz` is the exception: it is a separate Rust project, not a
  TinyTorrent2 dependency, and agents may build and test it under its own
  `AGENTS.md`.

## Code style

The best code is the simplest correct code whose intent and ownership are
obvious on first reading. Five principles decide every change; the points under
each say how to apply it. The test: an engineer new to the feature finds what
an operation does, where its rules live, and how its data changes by reading
the code, without a map of the architecture.

1. **Correctness first.** Code is correct, secure and reliable, and fast where
   performance is measured. Simplicity never justifies weaker behavior, so
   correctness wins any conflict with the principles below.
2. **Clarity over cleverness.** Write for the reader, because code is read and
   changed far more often than it is written. Intent is obvious without
   tracing abstractions or indirection.
   - Choose the most appropriate algorithm and data structure for the problem. Consider time and space complexity, expected scale, and proven library implementations. Prefer efficient, established algorithms over simpler but inferior ones. Among comparably effective solutions, choose the easiest to understand and maintain. Keep control flow straightforward, names and types expressive, and execution paths easy to trace without unnecessary indirection.
   - **Choose the right model for the behavior.** Evaluate booleans, enums, discriminated unions, state machines, polymorphism and ordinary branching according to the problem. Use booleans for independent facts, enums for mutually exclusive states, state machines for constrained transitions, and polymorphism when implementations differ in behavior. Interacting flags, impossible combinations, repeated conditions and scattered transitions signal that the model may be wrong. Reconsider the representation instead of adding more branches. Choose the simplest model that expresses the rules correctly.
   - Requests, results and errors that cross a boundary have named types, not
     strings, loose JSON or the other side's storage shapes. A contract
     separates the failures its caller handles differently.
   - Make invalid states unrepresentable wherever the type system can: a
     comment asks every later writer to remember, while a type refuses for
     them. Give each state you design its own value, so an empty string, a
     zero or a sentinel you invent never selects a second behavior. A
     convention the platform or a library already defines, such as
     libtorrent's 0 for no limit, stays at that boundary, because readers
     already know it. Prefer explicit state and legal transitions over
     combinations of flags. In C#, use the
     null-forgiving `!` only where a real invariant guarantees the value;
     elsewhere it hides the null it claims cannot happen.
   - Dependencies, state changes, side effects and failures show at the call:
     a constructor receives what a type depends on, and a method that saves,
     sends or changes shared state says so in its name.
3. **One responsibility, one owner, one implementation.** Every behavior has
   one authoritative location, and related logic sits together in it, because
   two copies of a rule are free to drift.
   - Commands and gestures for the same operation call the same
     implementation. When the change exposes two implementations of one rule,
     move their direct callers to the established owner in the same change.
   - Independent owners meet at a small boundary, so a change stays inside one
     owner.
   - Validation, invariants, transactions and error handling each have one
     owner where data enters or leaves. Code inside that boundary trusts the
     result; a second check needs a reason you can name, such as untrusted
     input from a website.
   - Store facts and decisions; derive display values and other computed state
     when read. Keep a stored derived value only for a measured reason, and
     name it as derived.
4. **Minimal architecture that works.** Every layer, interface, abstraction,
   dependency and file earns its place by removing more complexity than it
   adds, because each one is another thing a reader holds or opens.
   - Each names the current requirement that forces it: it owns a concrete
     decision, hides necessary complexity, or improves cohesion or reuse.
     Shorter code does not count. Separate responsibilities do not require
     separate projects, interfaces or processes.
   - Use the language, .NET, WinUI and the C++ standard library directly, the
     way their documentation shows. A wrapper, factory or generic
     infrastructure around them needs a concrete benefit.
   - Build for the requirements that exist now and extend when a concrete need
     arrives, because speculative flexibility usually guesses the future wrong.
   - Where a host accepts plugins, such as Library's providers or the
     subtitle suppliers, the host owns the workflow every plugin shares:
     scheduling, cancellation, storage, matching policy and presentation. A
     plugin owns only what differs between implementations and takes only the
     code its behavior needs. It receives no host internals such as the store
     and repeats no host rule; a plugin that needs either shows the contract is
     wrong.
5. **Improve without expanding scope.** Solve the actual problem completely,
   and no more. Refactor when it materially simplifies the code or clarifies
   ownership, and prefer deleting complexity over moving it behind a new name.
   - Follow the existing solution to the same problem and match the existing
     style; "cleaner" and "more flexible" alone are not reasons to depart, and
     usability wins when it conflicts with consistency.
   - When the feature you are changing holds the worse solution, restructure
     it in the same change, because bad code kept for uniformity or to avoid a
     redesign is copied by the next change.
   - Look first for what to remove: pass-through layers, unneeded state,
     duplicate logic and obsolete paths. When a new implementation replaces an
     old one, remove the old one and its transitional layers in the same
     change; keep a compatibility path only for a real requirement, such as
     reading data an earlier version saved.
   - Outside that feature, remove only orphans the change creates and report
     other problems instead of widening the diff or building a third solution.

Read the final diff as the engineer who will own it. Every new file, type,
state, dependency and branch answers a concrete requirement, and the test above
holds.

## Naming

Read [naming and structure](docs/agents/naming.md) before naming or placing a type,
member, file, folder, namespace, or resource key, or before adding an interface
or a signature, including an enum, and run its naming review on every new or
changed name. One policy serves every project.

## Code comments

Read [code comments](docs/agents/comments.md) before adding, changing, or reviewing
comments or API documentation. Before reporting a code change complete, review
every comment the diff adds against that policy and remove the ones that fail.
One policy serves every project.
