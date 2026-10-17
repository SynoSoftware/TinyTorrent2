# Code comments

These rules apply across the repository. Read them before adding, changing or
reviewing comments or API documentation in any project.

**Comment only to preserve information a future maintainer needs that the code
does not make clear.** Before adding one, identify what useful knowledge would
be lost without it.

Review each added comment in the final diff. Remove it if a reader can recover
its point from the nearby code, names, types, UI text, or another comment. A
true explanation still costs attention when it preserves no otherwise missing
knowledge; this check prevents a file or method from accumulating narration as
it grows.

- **Explain the non-obvious.** State the purpose, constraint, invariant,
  ownership boundary, or consequence that matters. A brief explanation of dense
  logic is useful; narration of straightforward code is not. Describe verified
  behavior, never an invented rationale.
- **Be concise and complete.** Prefer one sentence. Add more only for distinct
  necessary information. Keep every live reason and the conditions that make it
  true; there is no line-count quota. Wrap new or edited comments naturally near
  80 columns, without jagged fragments.
- **Document usage contracts.** Public API documentation explains purpose and
  caller obligations the signature cannot express: units, side effects, failure
  conditions, ownership or lifetime. Use `<summary>` for the contract and
  `<remarks>` for essential qualifications. Public visibility alone does not
  justify a comment that merely repeats the name.
- **Give explanations one home.** Place them beside the code that owns the
  decision. Reference shared rules instead of copying them. For workarounds,
  include the relevant issue and removal condition when known.
- **Describe the software as it stands.** Keep task history, reviewer
  instructions and agent narration in the commit or PR. Retain historical or
  version details only when they establish a current constraint. Add no
  decorative headings or commented-out implementations.
- **Preserve existing knowledge.** Update, move or remove comments your change
  makes inaccurate or misplaced. Otherwise leave them alone unless comment
  cleanup was requested. During cleanup, compare against the original and
  restore any lost live reason; if its relevance is uncertain, retain it and
  flag the uncertainty. Never perform a bulk rewrite merely to enforce length or
  style.
