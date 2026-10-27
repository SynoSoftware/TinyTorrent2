# Specialist reviews (`deep`)

How the coordinator runs a `deep` review. The passes and the plan checks themselves live in [SKILL.md](SKILL.md).

## Orchestration

One coordinator owns the scope, findings, and every edit. Specialists only read and report.

1. **Prepare once.** Do the preparation in SKILL.md, then give every specialist the same evidence: the scope, the requirements, the dependencies Preparation named, the applicable `AGENTS.md` rules, the owners found, and the settled findings of any earlier review record, which stay settled as SKILL.md says. When you wrote the code, leave out the owners found, because your view of them anchors every specialist; each specialist finds them itself.
2. **Launch.** For code, start one specialist per pass, all at once, in one message where the host supports it. No specialist waits for another: each pass reads the same change through its own lens. When the `flow` pass's transition-table condition holds, one specialist runs `paths` and `flow` together, because both list the same events and only one of them should own where those events overlap. A plan review runs in series, as described below.
3. **Merge.** When all have reported, merge duplicates and challenge each finding that lacks the evidence SKILL.md requires. When you wrote the code, merge duplicates only: report every specialist finding and put your objection beside it, because an author approves its own design choices. Escalate a question as SKILL.md's Escalation section says.

With `fix`, every round after the first is a `delta` review, as SKILL.md says. For code, in round two one new Opus / Sol reviewer, briefed with the record, runs every pass on the delta, including interactions with affected existing behavior, and later rounds continue that reviewer; one specialist per pass would duplicate the shared evidence review for a small fix. Launch them all again only when SKILL.md requires a full review. For a plan, the reviewer and then the challenger review the delta.

Each specialist's brief includes its pass from SKILL.md (or, for a plan, its role below), the shared evidence, and the findings format from SKILL.md. Require a pass status and evidence-backed findings, in plain words. A specialist is done when its pass's completion condition holds and it has reported a status.

If a preferred model is unavailable, use the closest available model.

## Plan review (`plan deep`)

Two specialists review the plan in series:

1. The reviewer (Opus / Sol, default tier) reviews the plan with the plan soundness checks in SKILL.md and reports its findings.
2. After the reviewer reports, the challenger (Fable / Astra, top tier, subject to the model constraint below) receives the plan, shared evidence, and those findings. It confirms or refutes each finding with evidence, and challenges the architecture, control-flow model, and hidden assumptions the first review missed.

The coordinator resolves remaining disputes with evidence and returns one verdict.

## Models

These are routing preferences for the model family the host actually offers.

**Current constraint, OpenAI family only:** Astra has no available tokens. Never invoke Astra; use Sol 6.1 instead. This overrides every routing preference below. Keep the challenger as a separate review even when both stages use Sol.

| Role | Claude family | OpenAI family |
| --- | --- | --- |
| Top: highest judgment, highest cost | Fable | Astra |
| Default: most passes | Opus | Sol |
| Economy: narrow passes | Sonnet | Terra |
| Mechanical checks only | | Luna6 |

Escalate to Fable or Astra only for a consequential question the evidence cannot settle, as SKILL.md's Escalation section says. Luna6 is far weaker than Terra: give it preparation, inventory, and narrow checks that a stronger reviewer verifies, never a whole pass.

## Code review routing (`deep`)

Each pass runs on this model; a question it cannot settle escalates as SKILL.md's Escalation section says:

| Pass | Model |
| --- | --- |
| Spec | Sonnet / Terra |
| Paths | Opus / Sol |
| Flow | Opus / Sol |
| Architecture | Opus / Sol |
| Conventions | Sonnet / Terra |
