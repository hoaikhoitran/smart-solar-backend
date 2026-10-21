-- PROPOSAL ONLY — NOT APPLIED, NOT PART OF ANY MIGRATION. Run manually only after written business approval.
--
-- Contracts signed before installation warranty options existed have customer_contract.warranty_months = NULL. The application
-- treats them as "warranty NOT_RECORDED": nothing breaks, no duration is invented, and a Manager cannot choose INSTALLATION_WARRANTY
-- coverage for them. Recommended default: leave them as they are.
--
-- If the business confirms, contract by contract, the warranty duration that is printed on the SIGNED document, this template records
-- exactly those values. Safety rules built in:
--   * only the contract ids listed below change; nothing is derived or defaulted;
--   * only rows that still have NULL months and are SIGNED or PAID (the signed versions customers hold) are touched;
--   * canonical_hash, the signed document and every other column stay unchanged (the stored hash was computed without months;
--     the backfill is documented by the audit event instead of re-signing);
--   * every change writes a contract_event 'WARRANTY_BACKFILLED' with the approver and the approval reference;
--   * the script refuses to commit when a listed contract is not eligible.
--
-- Fill in :approver_user_id (an Admin's user id) and the VALUES list, then run inside a transaction on the target database.

BEGIN;

CREATE TEMP TABLE legacy_warranty (contract_id uuid PRIMARY KEY, warranty_months int NOT NULL CHECK (warranty_months BETWEEN 1 AND 240),
    approval_reference varchar(200) NOT NULL) ON COMMIT DROP;

INSERT INTO legacy_warranty (contract_id, warranty_months, approval_reference) VALUES
    -- ('00000000-0000-0000-0000-000000000000', 120, 'Signed contract HD-..., page 3, approved by ... on ...'),
    (NULL, NULL, NULL)   -- remove this placeholder line; the NOT NULL constraints stop the script while it is present
;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM legacy_warranty l LEFT JOIN customer_contract c ON c.id = l.contract_id
               WHERE c.id IS NULL OR c.warranty_months IS NOT NULL OR c.status NOT IN ('SIGNED', 'PAID')) THEN
        RAISE EXCEPTION 'Backfill refused: a listed contract does not exist, already has warranty months, or is not SIGNED/PAID.';
    END IF;
END $$;

INSERT INTO contract_event (id, contract_id, event_type, from_status, to_status, actor_user_id, detail, created_at)
SELECT gen_random_uuid(), c.id, 'WARRANTY_BACKFILLED', c.status, c.status, :'approver_user_id'::uuid,
       left(format('warranty_months NULL -> %s; %s', l.warranty_months, l.approval_reference), 500), now()
FROM legacy_warranty l JOIN customer_contract c ON c.id = l.contract_id;

UPDATE customer_contract c SET warranty_months = l.warranty_months
FROM legacy_warranty l WHERE c.id = l.contract_id AND c.warranty_months IS NULL;

-- Review the result, then COMMIT (or ROLLBACK).
SELECT c.id, c.contract_number, c.status, c.warranty_months FROM customer_contract c JOIN legacy_warranty l ON l.contract_id = c.id;
-- COMMIT;
