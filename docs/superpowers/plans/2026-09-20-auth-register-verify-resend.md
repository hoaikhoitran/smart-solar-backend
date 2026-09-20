# Local Registration, Email Verification and Resend — Implementation Plan

**Goal:** Implement `POST /api/auth/register`, `POST /api/auth/verify-email` and `POST /api/auth/resend-verification` on the existing Identity schema, with no schema change.

**Architecture:** Api controllers map HTTP to module commands; handlers in `SmartSolar.Modules/Identity` own the business rules and depend only on Module-owned interfaces; `SmartSolar.Infrastructure` implements those interfaces over `AppDbContext`, ASP.NET Core password hashing and the existing `IIntegrationEventPublisher`. Each write path runs in one EF transaction, commits, and only then publishes the integration event.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, FluentValidation, ASP.NET Core rate limiting, MassTransit (publisher boundary only), xUnit + SQLite in-memory + Mvc.Testing.

**Spec:** the task instructions in this conversation (register/verify/resend, YAGNI schema rule, anti-enumeration rules, envelope contract).

## Global Constraints

- No schema change, no new migration, no `dotnet ef database update`.
- No commit, no push, no branch or worktree change. Branch stays `feature/auth-register`.
- Modules must not reference Infrastructure or Api.
- Envelope property names exactly: `isSuccess`, `traceId`, `data`, `error`.
- Email normalization: `Trim().ToLowerInvariant()`, stored directly in `user_account.email`.
- Verification token: 32 random bytes (`RandomNumberGenerator`) → Base64Url raw token → SHA-256 hex stored in `auth_action_token.token_hash`. Raw token is never persisted, logged or returned.
- Token lifetime: 24h, from configuration (`Auth:EmailVerification:LifetimeMinutes` = 1440).
- Rate limits: register 5/10min, verify 20/10min, resend 3/15min, partitioned by client IP.
- System roles are seeded idempotently: CUSTOMER, SALES, TECHNICIAN, MANAGER, ADMIN.

## Review Focus

1. Duplicate email that differs only by case or surrounding whitespace must still return 409.
2. An OAuth-only account (`password_hash IS NULL`) must never gain a password via register or resend.
3. Resend must return an identical response body for missing, verified, OAuth-only and eligible accounts.
4. Resend token cleanup must delete only unused EMAIL_VERIFICATION rows for that user, never PASSWORD_RESET rows.
5. A failed event publish after commit must not roll back or fail the created account.

---

## File Structure

**SmartSolar.Modules/Identity**
- `Constants/RoleCodes.cs` — the five system role codes.
- `Constants/AuthErrorCodes.cs` — error codes used in the envelope.
- `Contracts/Persistence/IIdentityUnitOfWork.cs`, `IIdentityTransaction.cs` — persistence boundary.
- `Contracts/Security/IPasswordHashingService.cs` — hashing boundary.
- `Options/EmailVerificationOptions.cs`, `Options/FrontendOptions.cs` — non-secret settings POCOs.
- `EmailVerification/EmailVerificationTokenFactory.cs` — RNG raw token + SHA-256 hash.
- `Register/RegisterCommand.cs`, `RegisterResult.cs`, `RegisterCommandValidator.cs`, `RegisterHandler.cs`.
- `EmailVerification/VerifyEmailCommand.cs`, `VerifyEmailResult.cs`, `VerifyEmailCommandValidator.cs`, `VerifyEmailHandler.cs`.
- `EmailVerification/ResendVerificationCommand.cs`, `ResendVerificationResult.cs`, `ResendVerificationCommandValidator.cs`, `ResendVerificationHandler.cs`.
- `Events/EmailVerificationRequestedEvent.cs` — UserId, Email, FullName, VerificationUrl.

**SmartSolar.Infrastructure**
- `Persistence/IdentityUnitOfWork.cs` — implements the persistence boundary over `AppDbContext`.
- `Persistence/Seed/SystemRoleSeeder.cs` — idempotent role bootstrap.
- `Security/PasswordHashingService.cs` — wraps `PasswordHasher<UserAccount>`.
- `Messaging/NullIntegrationEventPublisher.cs` — used when RabbitMQ is disabled.
- `DependencyInjection.cs` — register the above.

**SmartSolar.Api**
- `Contracts/ApiResponse.cs`, `Contracts/ApiError.cs` — envelope.
- `Extensions/ApiResponseFactory.cs` — envelope construction with traceId.
- `Extensions/RateLimitingExtensions.cs` — three named policies + 429 envelope + Retry-After.
- `Middlewares/ExceptionHandlingMiddleware.cs` — unhandled errors → 500 envelope, logged.
- `Controllers/AuthController.cs` — three endpoints.
- `Contracts/Auth/*.cs` — request DTOs.
- `Program.cs`, `appsettings.json` — wiring and non-secret config.

**tests/SmartSolar.Tests**
- `Unit/Identity/*` — handler tests over SQLite + real unit of work.
- `Integration/Auth/*` — endpoint tests via `WebApplicationFactory`.
- `TestSupport/*` — SQLite context factory, fake publisher, web factory.

## Task Order

1. Test project packages and support harness.
2. `EmailVerificationTokenFactory` (RED → GREEN).
3. Persistence boundary + `IdentityUnitOfWork` + role seeder.
4. `RegisterHandler` (all register cases).
5. `VerifyEmailHandler` (all verify cases).
6. `ResendVerificationHandler` (all resend cases, including PASSWORD_RESET isolation).
7. Api envelope, exception handling, rate limiting, controller.
8. Endpoint tests, including 429 envelope.
9. Full build + test run, code review, verification pass.
