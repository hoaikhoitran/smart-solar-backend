# Verification Email Delivery Implementation Plan

**Goal:** Complete the delivery path: RabbitMQ → `EmailVerificationRequestedConsumer` → `IEmailSender` → MailKit SMTP.

**Architecture:** The message contract stays in Modules; the consumer, SMTP sender and options live in Infrastructure. The consumer is a thin MassTransit adapter over `VerificationEmailService`, which composes the email and calls `IEmailSender`. That split keeps the delivery behavior testable without a bus; it was originally forced by the v9 licensing constraint (now superseded) and remains useful on its own merits.

**Tech Stack:** .NET 8, MassTransit 8.5.10 + MassTransit.RabbitMQ 8.5.10, MailKit, xUnit.

**Spec:** the task instructions in this conversation.

## Global Constraints

- **SUPERSEDED 2026-09-21: the stack moved to MassTransit 8.5.10.** The original constraint read: "MassTransit 9.2.2 requires a commercial license to start any bus, including the in-memory transport used by its test harness (`ConfigurationException: [Failure] License must be specified...`). Do not downgrade." That prohibition no longer applies — the downgrade to the Apache-2.0 8.x line was made deliberately, because v9 blocked every bus start and therefore all RabbitMQ runtime use. Verified on 8.5.10: an in-memory bus starts, consumes and stops with no license, and the API now fails on broker connectivity (`BrokerUnreachableException`) instead of licensing. Do not restore 9.x without a license.
- **Support horizon:** the 8.x line receives security and critical fixes through at least the end of 2026. Revisit before then: buy a v9 license or move to a maintained fork.
- **Consequence for tests:** a MassTransit in-memory test harness is now possible (it was not under v9). The delivery path still has no harness-level test; that is now a choice, not a constraint.
- No schema change, no migration, no `database update`.
- No commit, no push, no staging. Branch stays `feature/auth-register`.
- Modules must not reference Infrastructure or Api; MailKit goes only to Infrastructure.
- Never log SMTP or RabbitMQ credentials, the raw token, or the VerificationUrl.
- Do not change the three auth endpoints, the envelope, statuses, or token generation/hashing.
- Existing 68 tests must stay green.

## Review Focus

1. `Email.Enabled=false` must not demand credentials, and the app must still start.
2. `RabbitMq.Enabled=true` with `Email.Enabled=false` must not silently look like working delivery.
3. A `FullName` containing HTML must be encoded in the email body, not injected raw.
4. An SMTP failure inside the consumer must propagate so MassTransit retries; it must not be swallowed like the publisher-side failure.
5. TLS must never fall back to an unencrypted or unvalidated connection.

---

## File Structure

**SmartSolar.Modules**
- `Common/Email/IEmailSender.cs` — minimal contract (`EmailMessage` record + `SendAsync`), no SMTP types.

**SmartSolar.Infrastructure**
- `Email/EmailOptions.cs` — `Email` / `Email:Smtp` options.
- `Email/EmailOptionsValidator.cs` — validates only when enabled; never echoes secret values.
- `Email/SmtpSecurity.cs` — maps (UseSsl, Port) to MailKit `SecureSocketOptions`; pure and testable.
- `Email/SmtpEmailSender.cs` — MailKit implementation: connect, authenticate when configured, send, disconnect.
- `Email/DisabledEmailSender.cs` — used when `Email.Enabled=false`; logs a warning, sends nothing.
- `Email/VerificationEmailService.cs` — composes subject/body (HTML-encoded name) and calls `IEmailSender`.
- `Messaging/RabbitMQ/Consumers/EmailVerificationRequestedConsumer.cs` — thin `IConsumer` adapter.
- `DependencyInjection.cs` — email registration plus the consumer and its receive endpoint on the existing bus.

**tests/SmartSolar.Tests**
- `Unit/Email/VerificationEmailServiceTests.cs`
- `Unit/Email/EmailOptionsValidatorTests.cs`
- `Unit/Email/SmtpSecurityTests.cs`
- `Unit/Email/MessagingRegistrationTests.cs`
- `TestSupport/FakeEmailSender.cs`

## Task Order

1. `IEmailSender` contract + fake (RED → GREEN).
2. `VerificationEmailService`: recipient, subject, link, HTML encoding, exception propagation, cancellation.
3. `EmailOptions` + validator rules.
4. `SmtpSecurity` TLS mapping + `SmtpEmailSender`.
5. Consumer adapter.
6. DI: email senders, consumer registration, single bus, `email-verification` endpoint with 3 bounded retries.
7. Full build + test + verification + code review.
