# Subtitles: release assessment

Evidence reviewed 2026-10-08. This records release requirements and unresolved
facts; [Automatic subtitles](subtitles.md) owns the experience. It does not
certify legality or Store acceptance. The prototype may proceed while these
release items are resolved.

## Provider authorization

OpenSubtitles' [API overview](https://opensubtitles.tawk.help/article/about-the-api)
explicitly offers professional packages for commercial applications. Its
[Pro packages guide](https://opensubtitles.tawk.help/article/pro-packages)
describes application subscriptions assigned to consumer keys, including access
without end-user authentication. Commercial integration is therefore a supported
route, not categorically prohibited. An end-user VIP account and an application
subscription are different arrangements. The owner's selected default is an
application-funded package without end-user accounts. Procuring and provisioning
it is TinyTorrent's responsibility; users are not asked to buy or configure access.

The [website terms](https://opensubtitles.tawk.help/article/terms-of-service)
and [legal information](https://opensubtitles.tawk.help/article/legal-information)
do not establish the scope of a TinyTorrent API agreement; the latter discusses
the older .org website and other products. Neither a library's open-source
license nor API availability licenses all subtitle content. The linked
[subscription page](https://opensubtitles.stoplight.io/docs/opensubtitles-api/fcgiyz3p7sqn9-api-subscription-prices)
yielded no readable terms in this review. No signed agreement, approved consumer
registration, or applicable plan was supplied for TinyTorrent.

Resolve the actual agreement's scope: desktop and Store distribution, commercial
use if applicable, automatic and early searches, local downloads, permitted key
distribution, quotas, attribution, and treatment of rights complaints. An
explicit applicable published grant suffices; request written clarification only
where those terms leave a material gap. Obtain account-free access for the default.
Apply the same check to each supplier before it is offered as working.

## Permission and privacy

On the review date, [Store policy 7.19](https://learn.microsoft.com/en-us/windows/apps/publish/store-policy-archive/store-policy-7-19)
is in force. Sections 10.5.1, 10.5.2, and 10.5.4 address privacy disclosure,
affirmative permission for third-party sharing, withdrawal, and secure handling.
Win32 applications require a privacy policy. Section 11.2 requires a lawful
basis for third-party content. These obligations do not prescribe a separate
checkbox or a modal consent screen.

**Design interpretation:** the disclosed Off-to-On action in the feature design
provides the affirmative choice, and Off withdraws it. Setup navigation alone
does not. No movie information is necessary to check provider access. This is
an implementation approach to validate against the final agreement and data
flow, not a guarantee of certification.

The owner selected the existing torrent proxy/adapter route for every subtitle
request, with no direct fallback when unavailable. This is settled product behavior;
transport support and route verification remain implementation work. Check and
authentication obey it as well as searches and downloads, so setup cannot bypass
the user's route. The release policy must describe the verified implementation.

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
connected to the publisher/provider's real handling process. That can preserve
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
| Provider release | TinyTorrent's applicable API/content permission and access arrangement are unverified. Provider guides establish the available route; Store 11.2 establishes the content-rights obligation. | Record the applicable grant/agreement and plan, resolve material scope/key-distribution/attribution questions, and verify authorized search/download access. |
| Feature release and Store | The actual privacy policy, publisher contact, API data handling, and network routing have not been established. Store 10.5 and applicable privacy law govern this. | Publish the accurate policy, link it in-app and in Partner Center, and verify disclosed payloads, credential handling, enable/disable behavior, recipients, retention and any applicable transfers. |
| Store only | The treatment of provider-contributed subtitles under UGC and age-rating provisions is unresolved. Sections 11.11/11.12 supply a concrete question, not proof of rejection. | Document applicable treatment for the submission date; implement any required unobtrusive reporting/source information and content access control, or retain authoritative evidence that a provision does not apply. |

These are release checks, not reasons to remove early matching, background work,
languages, or quiet recovery. No provider was contacted, account created,
subscription purchased, or terms accepted during this review.
