# Coordination

An agent that leads other agents owns the outcome: a correct product, delivered
fast. The owner pays for each wasted round in hours of a shared 15 W laptop,
and for each unchecked report in defects found later. These steps cover what
leading adds to the rules for any task, in the order the work meets them.

## 1. Trace before planning

Before the plan exists, trace every library behaviour and every wait path the
design depends on: how the library schedules the work, and how each job can
wait, block or hang. Trace them yourself, or through an agent whose material
findings you confirm in the code. A dependency found after the build costs a
full round of building, testing and review; found before, it costs a paragraph
of the plan.

Done when every behaviour and wait path the design depends on is answered from
the source, the documentation or a measurement, and every rare edge case has
one sentence of reasoning, and the plan states the estimate in rounds and
hours. A question the design depends on is never left open.

## 2. Price every scope change

When the owner adds to the request during the work, state what it adds in
rounds, hours and risk before you start it. Then do it in the order received,
unless the owner defers or drops it. The owner then steers with the cost in
view, and a growing task never surprises them at the end.

Done when each addition's cost was stated before work on it began.

## 3. Keep one source of truth

Write each owner decision into the working plan the moment it is made. It moves
into the contract with the implementation, because a contract describes how the
product works now. A correction to a running agent names the plan section and
the new decision. Agents read a chain of messages out of order and keep part of
it; a section they reread does not drift.

Done when an agent could do the work from the plan and the contracts alone.

## 4. Split only what runs in parallel

Split work only where pieces can run at the same time without waiting for each
other, along file ownership such as engine, app and contracts. Give the rest to
one agent, because each extra agent competes for the shared laptop. Two agents
never edit one file, because the second edit overwrites or conflicts with the
first and neither diff then has one owner. Fix the contract between pieces
before they start: the pipe names and fields, and which files each agent owns.

A measurement goes to the agent that already has its context. Before you start
a piece or a spike, search the [shared record of findings](testing.md#the-everyday-loop), the plan
and the agents' reports for its answer, and start it only when no agent has the
answer or is working on it.

Done when each piece names its files, its contract and who builds.

## 5. Brief with the build plan

Each brief carries the findings the agent cannot look up cheaply:
measurements, traced library behaviour and owner decisions, so the agent starts
from them instead of finding them again. It also names the agent that builds, the
checks this piece needs, and that every other agent hands its finished edits to
that builder. Where a material correctness risk exists, you start an
independent adversarial review, run as a
[focused review pass](compilation.md#focused-review-passes), once the edits are
finished. The brief tells the builder to build when you report that the
review's fixes are in, so defects are fixed before the one build. The [compilation](compilation.md) rules then hold
across agents: one build for the round's collected questions, instead of one
per agent.

Done when the agent can start without asking and knows whether it builds.

## 6. Run checks in the background

Run long checks in the background and act on their completion notice, so the
lead keeps coordinating while a check runs and the owner sees each result when
it lands. [Testing](testing.md) decides which checks a round needs.

Done when no agent waits on a long check by sleeping.

## 7. Check every report

Treat a report as a claim. Before passing a result to the owner:

- Read the diff against the [code style](../../AGENTS.md#code-style) and the
  [focused review passes](compilation.md#focused-review-passes).
- Set each limit from the wait a person would accept, because the person sits
  through it. A limit computed as another limit plus a margin shows that nobody
  chose it: replace it, and keep any order between the two limits that the
  design needs.
- Confirm each material claim yourself, in the code line, the test output or the
  measurement.

Done when every claim you relay is checked, or marked unverified with what you
tried.

## 8. Report the real cost

Each report to the owner states what the work really cost in time against the
estimate, and states your own mistakes plainly, with what changes because of
them.

Done when the owner can steer from your last message alone.

## 9. Clean up after checks

Stop the agents, engines and background runs you started, and delete the
temporary data folders their checks created. Keep the source edits and the
build outputs, because the next build reuses them.

Done when nothing you started still runs and no temporary data folder remains.
