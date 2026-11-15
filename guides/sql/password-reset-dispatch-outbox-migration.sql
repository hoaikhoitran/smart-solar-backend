-- PasswordResetDispatchOutbox (20261115153617): transactional outbox of password-reset emails (one new table).
-- Generated with `dotnet ef migrations script 20261115153613_IotEnergyMonitoring 20261115153617_PasswordResetDispatchOutbox --idempotent`
-- for review only. NOT APPLIED anywhere. Down = DROP TABLE password_reset_dispatch (pending reset emails are lost; tokens stay valid).

﻿START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153617_PasswordResetDispatchOutbox') THEN
    CREATE TABLE password_reset_dispatch (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        action_token_id uuid NOT NULL,
        email character varying(320) NOT NULL,
        full_name character varying(200) NOT NULL,
        reset_url character varying(2048),
        status character varying(20) NOT NULL,
        publish_attempts integer NOT NULL,
        next_attempt_at timestamp with time zone NOT NULL,
        last_error character varying(200),
        created_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        published_at timestamp with time zone,
        sent_at timestamp with time zone,
        CONSTRAINT "PK_password_reset_dispatch" PRIMARY KEY (id),
        CONSTRAINT ck_password_reset_dispatch_attempts CHECK (publish_attempts >= 0),
        CONSTRAINT ck_password_reset_dispatch_status CHECK (status IN ('PENDING', 'PUBLISHED', 'SENT', 'EXPIRED')),
        CONSTRAINT ck_password_reset_dispatch_url CHECK ((status = 'PENDING') = (reset_url IS NOT NULL)),
        CONSTRAINT fk_password_reset_dispatch_action_token FOREIGN KEY (action_token_id) REFERENCES auth_action_token (id) ON DELETE CASCADE,
        CONSTRAINT fk_password_reset_dispatch_user FOREIGN KEY (user_id) REFERENCES user_account (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153617_PasswordResetDispatchOutbox') THEN
    CREATE INDEX ix_password_reset_dispatch_status_next_attempt ON password_reset_dispatch (status, next_attempt_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153617_PasswordResetDispatchOutbox') THEN
    CREATE INDEX ix_password_reset_dispatch_user ON password_reset_dispatch (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153617_PasswordResetDispatchOutbox') THEN
    CREATE UNIQUE INDEX ux_password_reset_dispatch_action_token ON password_reset_dispatch (action_token_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153617_PasswordResetDispatchOutbox') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261115153617_PasswordResetDispatchOutbox', '8.0.34');
    END IF;
END $EF$;
COMMIT;

