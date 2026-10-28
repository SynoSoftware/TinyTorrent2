---
name: code-review
description: Review a plan before coding, or review code changes since a Git reference (a branch, PR, commit, or work in progress) against requirements and repository conventions. Also fixes what it finds and reviews again until clean (fix), runs a stronger review with specialists or a second model (deep), re-reviews only what changed since an earlier review (delta), and audits the design of existing code with no change (audit).
---

# Code Review

Find real defects, unclear ownership, and avoidable complexity, and report each one with evidence. The review is read-only unless the user passes `fix`. A review reads code: it never compiles, builds, runs tests, or starts the application, because reading answers what a review asks, and a build or test run costs minutes it does not need. The only exception is `fix`, which may build and run focused tests once, after its last round.

## Commands

When the argument is `help` or `?`, print this help as it stands and stop:

```text
/code-review [what] [deep] [fix]

What to review (default: the code you have not committed)
  (nothing)        the code you changed and have not committed
  <git ref>        the code changed since a branch, commit or PR
  plan <plan>      a plan, before anyone writes code: its file, or paste it
  audit <path>     the design of existing code in a folder or owner, with
                   no change: state, transitions, waits and ownership
  <pass>           one code pass only: spec, paths, flow, architecture
                   or conventions

How (add any of these)
  deep             a stronger review. A plan gets a second, stronger model
                   that challenges the first review. Code gets one
                   specialist per pass, at the same time.
  fix              fix the high and medium findings the change caused
                   and review again, round after round, until none
                   remains. Findings that were already there are
                   reported, not fixed. Each later round reads only what
                   the fixes changed and what that affects.
  delta <record>   review only what changed since an earlier review's
                   record, such as after someone else fixed its findings.

Examples
  /code-review                        review my uncommitted code
  /code-review fix                    review it, and fix it until clean
  /code-review main deep fix          strongest review since main, fixed
  /code-review audit src/engine       find design weaknesses no change
                                      has touched
  /code-review plan docs/plans/x.md deep fix
                                      strongest plan review, revised
                                      until sound

From a request to reviewed code in one step: /feature <what you want>
```

The older words still work: `plan2` is `plan deep`, `code2` is `deep`, `code` is the default review, `polish` is `fix`, and `elegance` is `architecture`.

`fix` on a plan revises only the plan; implementation code stays untouched. Honor the user's limits on tests, builds, execution, and scope.

`deep` delegates to specialists: read [specialists.md](specialists.md) before launching any.

## Scope and owners

Every pass works on **the scope**, which each mode defines once:

- **Change review:** what the diff adds or changes, the state it writes, and the entry points that reach that state.
- **Audit:** all code in the given path. Everything in it counts as present; nothing counts as added or changed.
- **Plan:** the writes and decisions the plan will add, read against the current code.

An **owner** is the type or module that holds a piece of state, together with the code that writes it.

## Who reviews

A code review is done by an agent that did not write the code, because an author approves its own design choices.

With `deep`, the specialists are the reviewers, as [specialists.md](specialists.md) says. Otherwise, if you wrote or edited any of the code under review in this session, launch one fresh subagent on the default-tier model (Opus / Sol) to review it. Use a fresh agent, not a fork, because a fork inherits your reasoning. Brief it with the scope (the Git reference or the files), the user's requirements, the arguments other than `fix`, and the path of this `SKILL.md`. It does the preparation and runs the passes in series itself, and returns the report with its review record. Give it the requirements, not your explanation of the design, because that explanation anchors it on your choices. With `fix`, you make the fixes and the subagent reviews each round, as [Review record and `delta`](#review-record-and-delta) says.

If you did not write the code, review it yourself. If the host cannot launch a subagent, run the passes yourself in series, including those `deep` would delegate, and say so in the report; with `fix`, report your fixes as unreviewed.

## Preparation, once per review

1. **Plan reviews:** obtain the plan and its intended outcome. Label a missing detail as an assumption.
2. **Code reviews:** validate the Git reference, or, with no reference, take the uncommitted changes: staged, unstaged, and relevant untracked files, because `git diff base...HEAD` sees only commits. With a reference, read code outside the diff from a commit with `git show <commit>:<path>`, because the working tree can hold another session's uncommitted work. A claim about later code names its commit, or says it is uncommitted. Leave Git state and unrelated changes as they are.
3. Take the requirements and constraints from the user's input. Search the issue tracker, commits, or project docs only when the input names one of them or states no requirement. When none exist, proceed and state what cannot be verified. An audit skips this step unless the user names requirements, and reads the contracts as sources of invariants.
4. Read the applicable `AGENTS.md` files, documented standards, nearby implementations, callers, contracts, and established owners.
5. When the scope calls into or changes a forked or vendored dependency, and in every audit, name each such dependency, its source path, the build the product links, and whether that build holds the dependency's current source. A project without one skips this step.
6. Split a large change or plan into coherent features. A single reviewer finishes one feature before starting the next. List the shared state that more than one feature writes, and run `flow` on it once, across those features, because each feature's review sees only its own events.

Preparation is done when you can name the scope, its requirements, and the owner of each responsibility it touches. A `delta` review takes its scope and its preparation from the review record instead, as [Review record and `delta`](#review-record-and-delta) says.

## Plan soundness (`plan`)

Challenge the approach before anyone implements it. A plan earns approval only when the repository evidence supports each premise.

1. **Goal and feasibility:** the plan solves the actual requirement within the real platform, dependency, repository, and contract constraints. Name each wrong assumption and missing prerequisite. Where a requirement depends on how a dependency behaves beyond its documented API, such as its threads, queues, or failure handling, trace that behavior in the dependency's source, and name each requirement the dependency cannot meet as it stands. A step that relies on a change to a dependency or a fork is done only when the product pins and builds that version, so the plan says when that happens.
2. **Behavior and state:** run the `paths` and `flow` checks below on the plan's scope. The plan names each invariant and the code that will enforce it. When the plan restructures existing code, it also names, for each invariant the restructuring could break and no existing check measures, the executable test that measures it and what a person would see if it broke, run before the restructuring starts. When the current owner already breaks its invariants or keeps them only by each writer remembering a step, the plan corrects that design before it builds steps on top, because each further patch adds another writer to a design that does not hold. Challenge stacked booleans, unsuitable algorithms, implicit ordering, and competing validation rules.
3. **Authority and fit:** find the current owner of each responsibility, and every place in the current code that makes each decision the plan changes. Challenge duplicate sources of truth, parallel implementations, unneeded abstractions, unrelated redesign, and UI controls that bypass established patterns.
4. **Simplest viable alternative:** for each material weakness, propose the smallest clearer approach and its tradeoff. Propose an enum, state machine, or polymorphism only where the rules need one.
5. **Verifiability:** the plan gives implementers clear boundaries and a way to demonstrate the intended behavior. Flag material risks it leaves open; leave out speculative infrastructure and surplus tests.

Return **Sound**, **Needs revision**, or **Unsound**, with a short rationale, the blocking issues and their repository evidence, the assumptions that remain unverified, and the minimal corrections. With `fix`, return a concise corrected plan.

## Design audit (`audit`)

Audit the design of existing code that no change touches, because a change review sees only what its change reaches, so a weak design elsewhere survives until a defect exposes it. Run `paths`, `flow`, and `architecture` on the path. Run `spec` only when the user names requirements, and skip `conventions`.

This section replaces [Who reviews](#who-reviews) for an audit.

1. **Prepare once for every agent.** Do the Preparation. When the path's waits reach a named dependency's I/O, trace that dependency's pools once, so no agent repeats it. A linked build that lacks source the path relies on is a finding.
2. **Split by owner, into as few agents as cover the path.** List the owners in the path. Put owners that share a writer or a resource in one agent, because a seam audited from both sides is audited twice; for example, a function that resets several owners' state puts those owners together. Every agent re-reads the shared files, so more than about five agents mostly repeat work. A path one agent can cover is audited by you alone, without step 3's verification, which would only re-read your own work. Launch the agents on the default-tier model, all at once where the host allows. Brief each with the path, its owners, the names of the other agents' owners, the Preparation results, and the path of this `SKILL.md`. Each audits only its own owners, without splitting again and without running steps 3 and 4: it runs the three passes and follows calls across its boundaries. It writes its own record, which holds the writer lists, invariants, lifetimes, tables, and waits with the pools they block. It returns its findings; the decisions, resources, and pools it shares with other owners; the questions it would escalate; and the files it did not read. The lead escalates those questions.
3. **Merge.** Merge duplicate findings. Verify each high or medium finding by reading its failure path in the code yourself, because one agent's claim is not yet evidence; drop a finding that does not hold, and list one that cannot be settled as unsettled, in one line. Then check what no single owner sees: a decision two owners make, a pool or worker that several owners' waits block, and an invariant that spans owners. Done when each shared decision, resource, and pool the agents returned has been checked.
4. **Report.** Open with a severity index, one line per finding. Then give the findings by owner: its invariants, the ones its design does not enforce, its broken table cells, and the correction its design needs. The lead's review record holds the file hashes and indexes the agents' records, which hold the writer lists, waits, and full tables.

An audit is read-only; `fix` does not apply, because its corrections are design work for the user's decision.

## Code-review passes

The default code review runs these passes in order in one reviewer, chosen as [Who reviews](#who-reviews) says. `deep` gives each pass to its own specialist, and all of them run at once. Each pass is done when its own completion line holds.

### 1. Specification (`spec`)

Map each relevant requirement to the behavior that implements it, or record the gap. Find missing, partial, incorrect, and unrequested behavior. Cite the requirement and the implementation. Separate an ambiguous requirement from a confirmed mismatch. When two requirements contradict each other, report the contradiction once for the user's decision, with the behavior each side gives. Behavior that one side allows is not a defect in any pass, because its fix would choose a side the user has not chosen.

The implementation must be in what ships: a change in a dependency counts only when the build the product links holds it, as Preparation step 5 found. Check the contract text the scope implements against the product rules the user has set: a contract that records a break of such a rule as current behavior is a gap, not a requirement.

Done when every requirement has an implementation in the shipped product or a recorded gap.

### 2. Execution paths (`paths`)

Trace each entry point in scope through validation, decisions, side effects, persistence, and cleanup. Where they apply, follow the normal, empty, error, cancel, retry, reentrant, concurrent, and shutdown paths. Look for bypassed guards, conflicting eligibility rules, unavailable recovery actions, and regressions. Report a bug only with a concrete path that fails.

Entry points are every event source that reaches the state in scope, not only commands: callbacks, events from libraries or the platform, worker completions, timers, startup, and shutdown. For each asynchronous operation in scope, and each one whose target the scope can replace, cancel, or remove, list what can change between its start and its completion: a newer request, a cancel, a replaced handle or object, a removal, other work on the same resource. A completion that acts without checking that its job is still current is a **late result**, and a defect when one of those changes is reachable. Check the reverse as well: for each job that replaces or removes an object other code uses, such as a handle, list every user of that object that can run before the job's completion, because those users act on an object the job may already have destroyed.

For each wait in scope (a lock, a queue, a thread pool, a future, a retry), name the event that ends it and the pool or worker it blocks. A wait with no ending event is a defect. Work blocked in a wait must hold nothing that unrelated work needs, because one hung resource then stops work that never used it. Two places hide such waits:

- **A dependency the product builds or vendors.** Its I/O runs on its own threads. Trace them once per dependency, in its source: which pools its I/O runs on and what else shares them. Record the result and reuse it. Platform I/O is documented and needs no trace.
- **A worker that serves one resource,** such as one drive or one server. List the resources each queued job touches, because a job that touches another resource blocks that worker on it.

Shutdown finishes in bounded time without destroying what running work still uses.

When the scope contains an action the person starts in the UI, such as navigating, opening, saving, deleting, or moving, trace its failure path from the person's side. They stay where they were, see in plain words why it failed, and can try something else. A view, selection, or focus that changes before the action succeeds is a defect, because a failure then leaves them somewhere they did not choose. While the action takes time, they see that it runs and can cancel it. Judge against what established Windows applications do and against Nielsen's usability heuristics. When the scope changes what a screen shows, such as its layout, controls, or text, the pass status names each question about the rendered screens that reading cannot settle, such as text that may wrap at a window size, as unverified, because a review that only reads code cannot see them. Such a question is not a finding, and neither the review nor `fix` inspects the screens.

Done when every event source that reaches the state in scope is traced, every asynchronous completion is checked for late results, every wait has its ending event and names the pool or worker it blocks and what else shares it, and every UI action the person starts has its failure path traced from the person's side.

### 3. Control flow and state (`flow`)

Code that is correct on its own can add the writer that breaks the rules of the state it writes, so this pass reads every writer of that state, not only the scope.

For each piece of shared state in scope (state that outlives one call and that more than one function reads or writes), search the codebase for **every writer**, and note why each one writes. Write the **invariants** the state must keep: the combinations the rules allow, the legal transitions, and any step every writer must take after writing. List the combinations the representation allows and the rules forbid. Treat fields that one rule constrains together as one piece of state, because most forbidden combinations span fields. Two writers that mean different things by the same value, such as a failure and a refusal, or a question and its answer, give it **two meanings**; that is a defect when a reader can act on the wrong one.

For each field the scope writes, shared or not, name its **lifetime** in one line: the event that starts the episode it belongs to, such as a failure, a request, or a question, and every event that ends that episode. A value that outlives its episode is a defect, such as a retry level that survives after its failure is gone, or an error kept after a new request.

Name the enforcer of each invariant: a type, or one function that every writer goes through. An invariant held only by a comment, or by each writer remembering a step (such as calling an apply function after changing one of its inputs), is a design finding even before anything fails; the writer list is its evidence.

When the state has a finite set of modes (an enum, a set of flags, a present or absent handle, a job in flight), and either an asynchronous job writes it or its invariants forbid some transitions, build its **transition table** from the code, because separate paths can each be correct and still fail when their events overlap. One row per mode, including each mode with a job in flight. The columns are the event sources `paths` traced, including each job's completion and shutdown. Each cell holds the next mode and the actions. A pair that a guard in the code rules out gets that guard's name instead of a full cell, so the table grows with the pairs that can happen; a pair ruled out only by belief, with no guard to name, gets a full cell, because that is where overlaps hide. An empty cell is an unplanned overlap, and a cell that breaks an invariant or the contract is a defect. Judge every cell as you fill it, because a wrong transition written into the table and not flagged is a defect missed with its evidence in hand. Keep the full table in the review record; the report names only empty and broken cells. When the scope alters a transition, recheck that mode's whole row and that event's whole column. When the scope alters a transition, or the table has an empty or broken cell, and no test runs sequences of these events and checks the invariants after each step, report the missing test as a finding. The finding names the overlap the test would catch and what the person would see if it broke, and meets the repository's rule for admitting a test where one exists, because a test earns its cost only by the failure it watches. It takes the severity of the worst cell it would catch, and is low when no cell is broken.

Question the algorithm and the state representation, not only whether each `if` works. Find stored facts that other state already implies, stacked flags, repeated decisions, and scattered transitions. Booleans hold independent facts, enums exclusive modes, explicit transitions sequencing that matters, and polymorphism genuinely distinct stable behavior. Recommend the simplest representation that makes legal behavior obvious; a framework or class hierarchy earns its place by the rules it enforces, not by the conditionals it removes.

Done when every piece of shared state in scope has its complete writer list, its invariants, and an enforcer for each invariant or a finding; every field the scope writes has its lifetime; and, where the table's condition holds, a transition table has every cell filled or reported.

### 4. Architecture (`architecture`)

Each responsibility has one owner and one authoritative implementation. Check that the scope uses the app's existing contracts and owners. For each decision in scope, such as whether an item may run or which problem it shows, search the codebase, including each dependency Preparation named, for other code that makes the same decision, because a rule implemented once in the product and once in its fork drifts like any other copy. Find duplicated business decisions, parallel implementations, misplaced responsibilities, redundant wrappers, and unneeded abstractions. In a change review, for each concept the diff adds (a state value, abstraction, helper, function split, or layer of indirection), ask what can disappear while behavior stays the same. Recommend a refactoring when it materially improves correctness or clarity, inside the scope, and never only because a smell's name fits.

Done when every decision and responsibility in scope has one named owner in the whole codebase, and, in a change review, every concept the diff adds is either needed or reported.

### 5. Naming and conventions (`conventions`)

Apply the `AGENTS.md` naming and comment rules and repository standards. Check each comment the scope adds or changes, and each comment whose code the scope changes, against the code it describes. Compare UI work with the app's real button hierarchy, dialogs, controls, menus, interactions, navigation, spacing, icons, and tokens. Reuse the established solution, and claim compliance only after inspecting real examples.

Done when every new name, every comment the scope adds or makes false, and every new UI element has been compared with the repository's rules or an existing example.

## Escalation

A review watches for these signs, so the user does not have to predict the risk. Each costs nothing until it fires.

- **An owner whose design the scope makes fail:** a design finding in it that the scope introduced or made worse (for example, by adding a writer that must remember a step), two or more such defects in the rules of its state (invariants, lifetimes, transitions, or late results) in one review, a fix that causes a new finding in it, or a change that fixes its state rules when the owner's last 20 commits (`git log -n 20 -- <owner's files>`) already hold two fixes to the same rules, because each fix in such a chain moved the defect instead of removing it. The defects found so far are symptoms of rules the design does not enforce, so an audit of the owner is the right next step, and it widens the review beyond what the user asked for, so the user decides. With `fix`, stop fixing that owner, finish the review of the scope, and then ask the user whether to audit the owner. Say what the problem is, with its evidence, such as the findings or the earlier fix commits, and name the owner's files. A reviewer that is a subagent returns the question to its caller. When the user agrees, run `paths` and `flow` on all of the owner, as an `audit` scoped to it, and report its invariants, the ones its design does not enforce, and the correction its design needs, for the user's decision. Without `fix`, report the failing design as a design finding whose correction is an audit of that owner, naming its files, and ask nothing, because the report is the work the user asked for. A failing design that the scope only found, without making it worse, is reported the same way, with origin "already there", and starts no audit, because every change that touches the owner would otherwise pay for the same audit again. An audit has already done this.
- **A consequential question the evidence cannot settle**, such as whether a transition is reachable or which owner should decide: send only that question, with its evidence, to the top-tier model in the Models section of [specialists.md](specialists.md), under its current constraint. When you cannot launch it, report the question as unresolved.

## Review record and `delta`

A review that has findings, or that a `fix` loop follows, writes a review record to a file in the session's scratch or temp directory and gives its path in the report, so that a later review can skip what this one covered and what has not changed since:

- **Reviewed:** for code, each file in the diff, untracked files included, and for an audit each file read, with the hash of the version reviewed: `git rev-parse <commit>:<path>` for a committed file, `git hash-object <path>` for an uncommitted one. When another session edits the files during an audit, audit the committed version. For a plan, a copy of the plan, because a plan is usually one file, and one changed line would change its hash and send the whole plan back for review.
- **Passes:** the passes or plan checks that reached their completion line.
- **Preparation:** the requirements, the rules applied, the owner of each responsibility, the dependencies Preparation named with their traced pools, and, for each piece of shared state `flow` covered, its writer list, invariants, and transition table, with the waits `paths` named.
- **Settled findings:** each finding with its outcome: fixed, rejected by the reviewer or the user with the reason, or waiting for the user's decision.

A `delta` review, and every round of a fix loop after the first, reviews only what changed since a record:

1. Find the changes. For code, they are the files whose hash differs from the record or that the record lacks, and any dependency the fixes changed, with the `spec` check that the product links it. For a plan, they are the sections that differ from the saved copy (`git diff --no-index`). Add what the changes affect: for code, the callers and callees, traced as far as the `paths` pass needs, the writers of the shared state the changes write, taken from the record plus any the changes add, and the rows and columns of any transition table the changes alter; for a plan, the steps that depend on a changed decision.
2. Reuse the record's preparation. Look up only the owners and rules that the changes newly touch, and any requirement added since the record.
3. Run the requested passes or plan checks on that scope. Everything outside it already passed, so it needs no second read.
4. Keep settled findings settled. Reopen one only with new evidence: the code or plan text it cites has changed, or a failure path its reason does not answer.
5. End with a new record that merges the old record with this review.

The reviewers of a delta did not write the fixes, because a fixer approves its own design choices. Where the host can continue an agent, send the delta to the previous round's reviewer, because it already holds the preparation; otherwise, put the record in a new reviewer's brief. [specialists.md](specialists.md) says how `deep` runs a delta.

Run the full review instead when no record exists, when the record lacks a pass, or when the changes alter a contract that unchanged code depends on, such as an interface, a saved format, or documented behavior, because tracing callers one at a time can miss a rule that spans several owners.

## Findings, fixes, and completion

- **What counts as a finding:** a reader acts on every finding, so each one must be proven and material. A **defect** has a failure path the reviewer traced in the code: the inputs or events that reach it and the line that does the wrong thing. A **design finding** has its writer list and shows an invariant that no type or single function enforces, either with a reachable defect or because every writer must remember a step. A **convention finding** breaks a written repository rule for names, comments, or UI, citing the rule and the line, or is a comment that states behavior, a reason, or an invariant the code does not have, because the next maintainer builds on it. A **risk** has a consequence that would be high but no failure path yet; report it in one line, only when that consequence is data loss, a crash, or a state nothing ends. Everything else stays out of the report: a comment or name that breaks no written rule, an unused field, a duplicate with no writer that breaks it, a style preference, or a claim the reviewer did not check.
- **Severity:** **high** is data loss, a crash, a state nothing ends, or an action the person did not ask for; **medium** is a wrong result or display with a way out, or a design finding with a reachable defect; **low** is any other design finding, and every convention finding. A defect is never low, because a failure the person can reach is worth the fix that `fix` gives only to high and medium findings.
- **Earlier conclusions:** when a finding contradicts a material conclusion in the review's inputs, such as an earlier review, audit, or plan, name that conclusion and settle the disagreement with evidence from the code, because a review is not right merely for being newer.
- **Origin:** each finding of a change review says whether the change introduced it, made it worse, or found it already there.
- **Review only:** list findings by severity, each with its pass, its origin, file and line, the concrete effect or failure path (for a design finding, the writer list and the invariant without an enforcer), the evidence, and a proportional correction. Separate defects, design findings, and risks, as defined above. Give a one-line status for every pass, including a pass that found nothing. Plan reviews return the soundness verdict instead, and an audit reports as its section says.
- **Review and fix (`fix`):** work in rounds. Round one is the full review the user asked for. In each round, fix each high or medium finding that the change introduced or made worse, and each convention finding the change introduced, because that fix stays inside the change. In code that a planned next step rewrites, fix only high and medium findings, because polish there is lost when that step lands. Report the rest without fixing them: a finding that was already there, because fixing it widens the change and edits code the request never needed to touch; a low design finding, because its fix costs more review than it saves; and a risk. Fix one coherent problem at a time, preserving unrelated changes; in a plan review, revise the plan. Fix a design finding in its owner's design, not by adding another check at one writer, and stop fixing an owner whose design is failing, as [Escalation](#escalation) says. Before the next fix, re-read the lines you changed and their direct callers, because a broken fix found there costs seconds and found by the next round costs a round; judging the design stays with the reviewer. After the last round only, build once and run the focused tests that reading cannot settle, as the repository's rules allow; review any change the build or tests force as one more `delta` round. Reuse the established contracts and UI standards, and stay inside the reviewed change and the owners its findings name. The next round is a `delta` review of those fixes against the record from before them. Stop when a round reports no high or medium finding the loop fixes; the convention findings a round fixes need no round of their own, because their fixes change no behavior.

  Report a finding that needs the user's decision, such as a major refactoring or a product choice, instead of fixing it; it does not keep the loop going. When a finding returns after its fix, stop retrying that finding and report it as unresolved, including the attempted correction. Continue addressing other findings.
- **Final verification:** the build and focused tests after the last round are the only checks; the last clean round has already reviewed the final code, so nothing runs a second time. A failure mode that reading cannot settle, such as a hang, a timeout, or a lost resource, needs a fault-injection test or a check against the real resource; without one, report it as unverified. Report the number of rounds, what each round fixed, the last review round's result, and what remains unverified.
