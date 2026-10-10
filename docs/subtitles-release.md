# Subtitles: release assessment

Evidence reviewed 2026-10-08. This records release requirements and unresolved
facts; [Automatic subtitles](subtitles.md) owns the experience. It does not
certify legality or Store acceptance. The prototype may proceed while these
release items are resolved.

## Supplier authorization

OpenSubtitles' [API overview](https://opensubtitles.tawk.help/article/about-the-api)
explicitly offers professional packages for commercial applications. Its
[Pro packages guide](https://opensubtitles.tawk.help/article/pro-packages)
describes application subscriptions assigned to consumer keys, including access
without end-user authentication. Commercial integration is therefore a supported
route, not categorically prohibited. An end-user VIP account and an application
subscription are different arrangements. The selected access model follows
[third-party provider access](architecture.md#third-party-provider-access): retain
the owner's existing free OpenSubtitles key; TinyTorrent funds no paid package.

Development does not require buying that package. Checked on 2026-10-09, the
supplier's Pro packages guide directs developers to create a Consumer and test
the application before choosing a paid plan. A free consumer API key can
establish the development integration. The
[getting-started guide](https://opensubtitles.tawk.help/article/getting-started)
requires a key on every request while allowing limited free downloads without
user login: five downloads per 24 hours per IP. Verify the free application-key
path and actual rate-limit/reset responses before release. The owner reports a
four-request per-IP limit; its time window still needs confirmation. Optional
login is separate from the application key; quota exhaustion never purchases access.

The production anonymous acquisition path passed on 2026-10-09 with the existing
free consumer key, yielding a valid 39,992-byte SRT. Its evidence and scope are
recorded in [implementation evidence](subtitles-implementation.md#existing-evidence-and-release-gates).
This establishes the observed download path; quota/reset responses and
distribution terms remain unverified.

The [website terms](https://opensubtitles.tawk.help/article/terms-of-service)
and [legal information](https://opensubtitles.tawk.help/article/legal-information)
do not establish the scope of a TinyTorrent API agreement; the latter discusses
the older .org website and other products. Neither a library's open-source
license nor API availability licenses all subtitle content. The linked
[subscription page](https://opensubtitles.stoplight.io/docs/opensubtitles-api/fcgiyz3p7sqn9-api-subscription-prices)
yielded no readable terms in this review. The owner reports an existing free
consumer key; the applicable distribution terms remain to be verified.

Resolve the actual agreement's scope: desktop and Store distribution, commercial
use if applicable, automatic and early searches, local downloads, permitted key
distribution, quotas, attribution, and treatment of rights complaints. An
explicit applicable published grant suffices; request written clarification only
where those terms leave a material gap. Verify the selected free integration.
Apply the same check to each supplier before it is offered as working.

SubDL's [terms](https://subdl.com/terms) permit an application, including a
commercial one, that lets each user bring their own API key; they forbid
pooling keys and asking for SubDL passwords. No attribution requirement was
found. Its [API documentation](https://subdl.com/developers) states that search
and download work on a free key, limited to 50 downloads a day. One user report
from June 2026 ([Bazarr issue 3393](https://github.com/morpheus65535/bazarr/issues/3393))
describes free keys receiving HTTP 402 on downloads. On 2026-10-09 the configured
development key downloaded one exact unpacked SRT over the fixed API origin
with HTTP 200; the production decoder preserved its 135,733 bytes. The
[implementation evidence](subtitles-implementation.md#existing-evidence-and-release-gates)
records its digest and scope. This proves that key's observed download access,
not every free plan. SubDL does not report a full/forced-only flag in the observed
response; the matching contract requires an exact release and language and rejects
explicit partial-subtitle labels, as defined in [Automatic subtitles](subtitles.md).

SubSource's [API documentation](https://subsource.net/api-docs) issues each
account its own key and sets rate limits per key. Its
[terms](https://subsource.net/terms) say nothing about API use by applications
and require that subtitles are not altered; no attribution requirement was
found. Its [privacy policy](https://subsource.net/policy) covers the website.
Ask SubSource in writing whether a distributed desktop application may let
each user bring their own key, because the published terms leave that gap.
Development testing with a developer key does not depend on the answer.

## Permission and privacy

On the review date, [Store policy 7.19](https://learn.microsoft.com/en-us/windows/apps/publish/store-policy-archive/store-policy-7-19)
is in force. Sections 10.5.1, 10.5.2, and 10.5.4 address privacy disclosure,
affirmative permission for third-party sharing, withdrawal, and secure handling.
Win32 applications require a privacy policy. Section 11.2 requires a lawful
basis for third-party content. These obligations do not prescribe a separate
checkbox or a modal consent screen.

**Design interpretation:** the disclosed Off-to-On action in the feature design
provides the affirmative choice, and Off withdraws it. Setup navigation alone
does not. No movie information is necessary to check supplier access. This is
an implementation approach to validate against the final agreement and data
flow, not a guarantee of certification.

Supplier operations follow the product's [network route](architecture.md#network-route).
Transport support and route verification remain implementation work. The release
policy describes the verified implementation.

Where GDPR applies, [the regulation](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng)
requires a lawful processing basis, transparency, minimization, and appropriate
security. Recital 32 expressly allows an affirmative technical setting as
consent. Articles 7 and 13 cover demonstrating consent, withdrawal, and the
information given to people. Do not assume consent is the only lawful basis for
every operation, or that it alone resolves international transfers. Confirm the
publisher's role, markets, recipients, retention and any applicable transfer
mechanism for the final service arrangement. No separate copyright-ownership
checkbox or per-movie confirmation is established by these sources.

The supplier's [privacy policy](https://opensubtitles.tawk.help/article/privacy-policy)
describes website processing, including IP logging. It is insufficient evidence
for a promise about current API retention, location, or onward recipients.
The [privacy draft](subtitle-privacy.md) deliberately leaves those facts pending.

## Microsoft Store submission

[Policy 7.20](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
is published but takes effect on 2026-10-22. It strengthens section 11.12 for
third-party user-generated-content integrations, including reporting and source
disclosure. Section 11.11.3 addresses content above an app's rating. Review the
version effective at submission rather than treating the forthcoming policy
as already in force.

Community-contributed subtitles make those content provisions relevant to
assess; saving to disk does not establish an exemption. Determine applicability
with the actual supplier and submission. If required, place source attribution
and an accessible Report a subtitle problem link in supplier information,
connected to the publisher/supplier's real handling process. That can preserve
the silent workflow without an in-torrent reporting panel. Additional account
or content-filter requirements depend on the actual rating/content rule, not a
universal subtitle-login assumption.

Store 7.19 sections 10.1 and 10.3 require accurate representation and a testable
product. Certification notes should describe early downloading faithfully and
provide a lawful movie/subtitle fixture and working access where needed. The
reviewed rules do not establish a blanket ban on early subtitle matching or
torrent clients; renaming functionality is not a rights or privacy remedy.

## Remaining release blockers

| Scope | Missing evidence or work | Completion evidence |
| --- | --- | --- |
| Supplier release | OpenSubtitles: TinyTorrent's applicable API/content permission and access arrangement are unverified; the free application key passed Check and production anonymous acquisition. SubDL: its terms permit each user's own key; production acquisition, explicit partial-label rejection and the 73-language mapping are verified. SubSource: its terms do not address application use. Store 11.2 establishes the content-rights obligation. | Record the applicable grant/agreement and plan, resolve material scope/key-distribution/attribution questions, and verify the remaining authorized search/download paths. |
| Feature release and Store | The actual privacy policy, publisher contact, API data handling, and network routing have not been established. Store 10.5 and applicable privacy law govern this. | Publish the accurate policy, link it in-app and in Partner Center, and verify disclosed payloads, credential handling, enable/disable behavior, recipients, retention and any applicable transfers. |
| Store only | The treatment of supplier-contributed subtitles under UGC and age-rating provisions is unresolved. Sections 11.11/11.12 supply a concrete question, not proof of rejection. | Document applicable treatment for the submission date; implement any required unobtrusive reporting/source information and content access control, or retain authoritative evidence that a provision does not apply. |

These are release checks, not reasons to remove early matching, background work,
languages, or quiet recovery. No supplier was contacted, account created,
subscription purchased, or terms accepted during this review.
