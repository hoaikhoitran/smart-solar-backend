-- =====================================================================================================================
-- DRAFT — DESTRUCTIVE CLEANUP AFTER THE BUSINESS-RULE REDESIGN — DO NOT RUN WITHOUT EXPLICIT WRITTEN APPROVAL
--
-- Status: NOT APPLIED anywhere. Not part of any EF migration. Never run automatically.
--
-- The migration 20261016161547_BusinessRulesFlow1And2Redesign is deliberately NON-DESTRUCTIVE: the objects below are no
-- longer in the EF model and no longer written by the application, but they still exist (with their data) so that the
-- redesign can be rolled back and the old data can be reviewed or archived first. This script removes them for good.
--
-- Preconditions (all required):
--   1. 20261016161547_BusinessRulesFlow1And2Redesign is applied and the release has been verified on that environment.
--   2. A fresh, verified backup (pg_dump -Fc + sha256) of the target database exists.
--   3. Every table below that still has rows has been archived (or the owner confirmed in writing it is not needed):
--        technician_travel_estimate (Supabase Dev: 3 rows — Vietmap route snapshots)
--        solar_simulation           (Supabase Dev: 20 rows — full simulation snapshots; the 11 selected ones already
--                                    have a compact copy in selected_simulation)
--        product.image_url          (Supabase Dev: 6 values — copied to product_image as primary images)
--   4. After this script, `dotnet ef migrations` Down of 20261016161547 is no longer possible (its Down expects these
--      objects). Convert this draft into a new EF migration (with an empty model diff) only after approval.
--
-- Rollback: only by restoring the backup from precondition 2.
-- =====================================================================================================================

BEGIN;

-- Guard: the redesign must be applied, and nothing may be dropped while it still holds unarchived rows.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261016161547_BusinessRulesFlow1And2Redesign') THEN
        RAISE EXCEPTION 'cleanup refused: 20261016161547_BusinessRulesFlow1And2Redesign is not applied';
    END IF;
    -- Remove a line below only after that table was archived (precondition 3).
    IF EXISTS (SELECT 1 FROM technician_travel_estimate) THEN RAISE EXCEPTION 'cleanup refused: archive technician_travel_estimate first'; END IF;
    IF EXISTS (SELECT 1 FROM solar_simulation) THEN RAISE EXCEPTION 'cleanup refused: archive solar_simulation first'; END IF;
    IF EXISTS (SELECT 1 FROM installation_shift_assignment) OR EXISTS (SELECT 1 FROM installation_team_member)
       OR EXISTS (SELECT 1 FROM installation_milestone) THEN
        RAISE EXCEPTION 'cleanup refused: legacy installation team/shift/milestone rows exist';
    END IF;
END $$;

-- Flow 1: Vietmap travel-time machinery (replaced by the configurable buffer between appointments).
DROP TABLE technician_travel_estimate;            -- FKs to technician_appointment and user_account go with it
DROP TABLE technician_schedule_recalculation;
ALTER TABLE technician_profile DROP CONSTRAINT IF EXISTS ck_technician_profile_base_paired;
ALTER TABLE technician_profile
    DROP COLUMN base_latitude,
    DROP COLUMN base_longitude,
    DROP COLUMN base_label,
    DROP COLUMN earliest_base_departure;

-- Solar simulation: full snapshots (results now live in Redis with a TTL; selections in selected_simulation).
-- Dropping the table also drops ak_solar_simulation_pre_survey_id_id, its checks, FKs and indexes
-- (ux_solar_simulation_reusable_fingerprint, ix_solar_simulation_pre_survey_id_created_at, ...).
DROP TABLE solar_simulation;

-- Installation Flow: team / shift / milestone model (replaced by one responsible technician per project).
DROP TABLE installation_milestone_evidence;
DROP TABLE installation_milestone;
DROP TABLE installation_shift_assignment;         -- drops ex_installation_shift_assignment_no_overlap with it
DROP TABLE installation_shift;
DROP TABLE installation_team_member;

-- Product catalog: the single legacy image (replaced by product_image).
ALTER TABLE product DROP COLUMN image_url;

-- Kept on purpose (still meaningful, NOT dropped):
--   technician_appointment.acknowledged_at   history of the removed acknowledge step (used by the redesign Down)
--   property_site.latitude / longitude       used by the solar simulation (PVGIS / NASA POWER)
--   btree_gist extension                     used by ex_technician_appointment_no_overlap and ex_installation_assignment_no_overlap

COMMIT;
