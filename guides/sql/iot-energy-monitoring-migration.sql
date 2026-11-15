-- IotEnergyMonitoring (20261115153613): three new tables for the IoT energy monitoring (device registry, bindings, telemetry).
-- One ACTIVE device per installation project and per device (partial unique indexes); a customer may own several projects.
-- Generated with `dotnet ef migrations script 20261114130345_InstalledAssetLotsAndPackageImage 20261115153613_IotEnergyMonitoring --idempotent`
-- for review only. NOT APPLIED anywhere. Apply through the approved process after review; until then the API answers 503 SCHEMA_NOT_READY.
-- Down = DROP of the three tables (see the migration class).

﻿START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE TABLE iot_device (
        id uuid NOT NULL,
        device_id character varying(64) NOT NULL,
        display_name character varying(150) NOT NULL,
        status character varying(20) NOT NULL,
        secret_hash character varying(64) NOT NULL,
        secret_version integer NOT NULL,
        secret_rotated_at timestamp with time zone NOT NULL,
        sensor_model character varying(50),
        max_current_amps numeric(9,3) NOT NULL,
        last_seen_at timestamp with time zone,
        disabled_at timestamp with time zone,
        disabled_by_user_id uuid,
        disabled_reason character varying(500),
        created_by_user_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        revision integer NOT NULL,
        CONSTRAINT "PK_iot_device" PRIMARY KEY (id),
        CONSTRAINT ck_iot_device_disabled CHECK ((status = 'DISABLED') = (disabled_at IS NOT NULL) AND (status <> 'DISABLED' OR (disabled_by_user_id IS NOT NULL AND disabled_reason IS NOT NULL))),
        CONSTRAINT ck_iot_device_max_current CHECK (max_current_amps > 0 AND max_current_amps <= 1000),
        CONSTRAINT ck_iot_device_secret_version CHECK (secret_version >= 1),
        CONSTRAINT ck_iot_device_status CHECK (status IN ('ACTIVE', 'DISABLED')),
        CONSTRAINT fk_iot_device_created_by FOREIGN KEY (created_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
        CONSTRAINT fk_iot_device_disabled_by FOREIGN KEY (disabled_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE TABLE iot_device_binding (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        project_id uuid NOT NULL,
        customer_user_id uuid NOT NULL,
        status character varying(20) NOT NULL,
        bound_at timestamp with time zone NOT NULL,
        bound_by_user_id uuid NOT NULL,
        note character varying(500),
        released_at timestamp with time zone,
        released_by_user_id uuid,
        release_reason character varying(500),
        CONSTRAINT "PK_iot_device_binding" PRIMARY KEY (id),
        CONSTRAINT ck_iot_device_binding_released CHECK ((status = 'ACTIVE') = (released_at IS NULL) AND (status <> 'RELEASED' OR (released_by_user_id IS NOT NULL AND release_reason IS NOT NULL))),
        CONSTRAINT ck_iot_device_binding_status CHECK (status IN ('ACTIVE', 'RELEASED')),
        CONSTRAINT fk_iot_device_binding_bound_by FOREIGN KEY (bound_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
        CONSTRAINT fk_iot_device_binding_customer FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
        CONSTRAINT fk_iot_device_binding_device FOREIGN KEY (device_id) REFERENCES iot_device (id) ON DELETE RESTRICT,
        CONSTRAINT fk_iot_device_binding_project FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
        CONSTRAINT fk_iot_device_binding_released_by FOREIGN KEY (released_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE TABLE iot_telemetry_reading (
        id uuid NOT NULL,
        device_id uuid NOT NULL,
        binding_id uuid NOT NULL,
        current_amps double precision NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_iot_telemetry_reading" PRIMARY KEY (id),
        CONSTRAINT ck_iot_telemetry_reading_current CHECK (current_amps BETWEEN -10000 AND 10000),
        CONSTRAINT fk_iot_telemetry_reading_binding FOREIGN KEY (binding_id) REFERENCES iot_device_binding (id) ON DELETE RESTRICT,
        CONSTRAINT fk_iot_telemetry_reading_device FOREIGN KEY (device_id) REFERENCES iot_device (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX "IX_iot_device_created_by_user_id" ON iot_device (created_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX "IX_iot_device_disabled_by_user_id" ON iot_device (disabled_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX ix_iot_device_status ON iot_device (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE UNIQUE INDEX ux_iot_device_device_id ON iot_device (device_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX "IX_iot_device_binding_bound_by_user_id" ON iot_device_binding (bound_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX ix_iot_device_binding_customer ON iot_device_binding (customer_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX ix_iot_device_binding_project_bound_at ON iot_device_binding (project_id, bound_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX "IX_iot_device_binding_released_by_user_id" ON iot_device_binding (released_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE UNIQUE INDEX ux_iot_device_binding_active_device ON iot_device_binding (device_id) WHERE status = 'ACTIVE';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE UNIQUE INDEX ux_iot_device_binding_active_project ON iot_device_binding (project_id) WHERE status = 'ACTIVE';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX ix_iot_telemetry_reading_binding_received_at ON iot_telemetry_reading (binding_id, received_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    CREATE INDEX ix_iot_telemetry_reading_device_received_at ON iot_telemetry_reading (device_id, received_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115153613_IotEnergyMonitoring') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261115153613_IotEnergyMonitoring', '8.0.34');
    END IF;
END $EF$;
COMMIT;

