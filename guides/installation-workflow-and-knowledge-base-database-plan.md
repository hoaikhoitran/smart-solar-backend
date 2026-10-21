# Installation Flow + knowledge base — database plan

> **STATUS: APPLIED to Supabase Dev on 2026-10-13** (`20261013182132_InstallationWorkflowAndKnowledgeBase`, together with
> pgvector in schema `extensions` and `knowledge_chunk.sql`). This page is the historical plan of that migration. The later,
> **not yet applied** redesign migration `20261016161547_BusinessRulesFlow1And2Redesign` replaces the installation
> team/shift/milestone model by one responsible technician and adds RAG configuration columns — see
> [database-migration-business-rules-redesign.md](database-migration-business-rules-redesign.md).

## 1. What changes

Migration: `20261013182132_InstallationWorkflowAndKnowledgeBase` (32 tables, 105 indexes, 83 foreign keys, 1 alternate key, manual exclusion constraint).

* **32 new tables** (EF model, `SmartSolar.Infrastructure/Persistence/Configurations/{InstallationWorkflow,KnowledgeBase,Files}/`) + **1 table owned
  by the Python RAG service** (`knowledge_chunk`).
* **No existing table, column, index or constraint is altered.** New tables reference existing ones
  (`user_account`, `survey_request`, `technician_appointment`, `property_site`, `product`).
  All 83 foreign keys are `ON DELETE RESTRICT` except `onsite_survey_obstacle.report_id` and
  `quotation_line.quotation_id` (`ON DELETE CASCADE`: rows that belong to their parent). 19 of them are emitted by EF as separate
  `ALTER TABLE … ADD CONSTRAINT` statements because the tables reference each other.
* Extensions: `btree_gist` (installation overlap exclusion) and `vector` (pgvector, RAG chunks).

## 2. Review artefacts

| File | Content |
|---|---|
| `guides/sql/installation-workflow-and-knowledge-base-schema-review.sql` | All 32 tables, indexes, checks and FKs **generated from the EF model**, plus a manual section (`btree_gist` + `ex_installation_shift_assignment_no_overlap`). Drift-checked by `PendingSchemaReviewTests`. |
| `src/SmartSolar.RagService/sql/knowledge_chunk.sql` | `CREATE EXTENSION vector`, `knowledge_chunk` with `vector(1536)` + HNSW cosine index. |

## 3. Approved rollout (owner)

1. Review both scripts (and the table summary below).
2. Either `dotnet ef migrations add InstallationWorkflowAndKnowledgeBase` (the generated migration must match the review
   script; then add the manual exclusion-constraint SQL to it with `migrationBuilder.Sql`) **or**
   apply the review script directly in one transaction.
3. Apply `knowledge_chunk.sql` (needs pgvector; on Supabase enable the `vector` extension first).
   The column is `vector(1536)` (`text-embedding-3-small`); the RAG service refuses any other dimension.
4. Back up first; the change is additive, rollback = drop the new tables (no data in old tables changes).
5. Deploy, then set `Features__InstallationWorkflowEnabled=true` and/or `Features__KnowledgeBaseEnabled=true`
   (the deprecated `Features__Flow2Enabled` still enables both when the new keys are absent).

Until step 5 the API never queries the new tables: Installation Flow / knowledge base endpoints answer 503
`FEATURE_PENDING_MIGRATION`, background workers idle, and Flow 1 scheduling ignores installation shifts.

## 4. Concurrency and integrity guarantees enforced by the database

| Guarantee | Mechanism |
|---|---|
| One DRAFT and one live quotation per survey request | partial unique `ux_quotation_one_draft`, `ux_quotation_one_live` |
| One live / one draft contract per quotation chain | `ux_customer_contract_one_live`, `ux_customer_contract_one_draft` |
| One payable and one PAID attempt per contract | `ux_payment_attempt_one_payable`, `ux_payment_attempt_one_paid` |
| Webhook idempotency | `ux_payment_webhook_event_payload` (provider, payload SHA-256) |
| Exactly one team leader | `ux_installation_team_member_one_leader` |
| No double booking of a technician on installation shifts | `ex_installation_shift_assignment_no_overlap` (manual SQL) + technician_profile row lock shared with survey booking |
| Unique serials | `ux_installed_asset_manufacturer_serial` |
| One pending acceptance request | `ux_installation_acceptance_one_pending` |
| A file backs one knowledge version | `ux_knowledge_document_version_file` |
| One published version per document | `ux_knowledge_document_version_one_published` |
| Ingestion idempotency | `ux_knowledge_ingestion_job_idempotency` |
| Chat retry idempotency | `ux_chat_message_client_id` (session, client_message_id) |
| Cost ledger never negative | `ck_rag_usage_daily_non_negative`; counters only ever grow (in-flight usage lives in `rag_usage_reservation`) |
| No stuck concurrency slot / no quota leak | leased `rag_usage_reservation` (`expires_at`); reserve and complete are single transactions with the ledger rows locked (global first); expired reservations are charged at their estimate (`estimated_tokens`) |
| Verified files always carry a lowercase SHA-256 | `ck_file_reference_verified_complete`, `ck_file_reference_checksum_format` (the S3 object version is enforced by the application, `Storage:S3:RequireObjectVersioning`) |
| A document's latest/published version exists and belongs to it | composite FKs `fk_knowledge_document_latest_version` / `fk_knowledge_document_published_version` → `ak_knowledge_document_version_id_document` (id, document_id) |
| Optimistic concurrency | `revision` columns (EF concurrency tokens) on every mutable aggregate |

`rag_usage_daily.user_id = 00000000-0000-0000-0000-000000000000` is the global ledger row (no FK by design).

## 5. Tables (generated from the review script)

### `payment_webhook_event`

- Columns: `id`, `provider`, `payload_sha256`, `order_code`, `provider_reference`, `amount`, `code`, `signature_valid`, `outcome`, `detail`, `received_at`
- PK: `id`
- Checks: `ck_payment_webhook_event_outcome`
- Indexes: `ix_payment_webhook_event_order_code` (order_code); **unique** `ux_payment_webhook_event_payload` (provider, payload_sha256)

### `rag_usage_daily`

- Columns: `user_id`, `usage_date`, `requests`, `input_tokens`, `output_tokens`, `estimated_tokens`, `updated_at`
- PK: `user_id, usage_date`
- Checks: `ck_rag_usage_daily_non_negative`

### `chat_session`

- Columns: `id`, `user_id`, `title`, `created_at`, `updated_at`
- PK: `id`
- FKs: `user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Indexes: `ix_chat_session_user_updated_at` (user_id, updated_at)

### `file_reference`

- Columns: `id`, `purpose`, `provider`, `bucket`, `object_key`, `original_file_name`, `declared_content_type`, `declared_size_bytes`, `declared_checksum_sha256`, `object_version_id`, `etag`, `verified_content_type`, `verified_size_bytes`, `verified_checksum_sha256`, `status`, `rejection_reason`, `verification_attempts`, `uploaded_by_user_id`, `verified_at`, `created_at`, `updated_at`, `revision`
- PK: `id`
- FKs: `uploaded_by_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_file_reference_checksum_format`, `ck_file_reference_purpose`, `ck_file_reference_sizes`, `ck_file_reference_status`, `ck_file_reference_verified_complete`
- Indexes: `ix_file_reference_uploader_created_at` (uploaded_by_user_id, created_at); **unique** `ux_file_reference_bucket_object_key` (bucket, object_key)

### `rag_usage_reservation`

- Columns: `id`, `user_id`, `usage_date`, `reserved_tokens`, `created_at`, `expires_at`
- PK: `id`
- FKs: `user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_rag_usage_reservation_lease`, `ck_rag_usage_reservation_tokens`
- Indexes: `ix_rag_usage_reservation_date` (usage_date); `ix_rag_usage_reservation_user_date_expires` (user_id, usage_date, expires_at)

### `chat_message`

- Columns: `id`, `session_id`, `role`, `content`, `status`, `client_message_id`, `reply_to_message_id`, `prompt_tokens`, `completion_tokens`, `model`, `latency_ms`, `created_at`
- PK: `id`
- FKs: `reply_to_message_id` → `chat_message`, `session_id` → `chat_session` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_chat_message_role`, `ck_chat_message_status`
- Indexes: `ix_chat_message_session_created_at` (session_id, created_at); **unique** `ux_chat_message_client_id` (session_id, client_message_id) WHERE client_message_id IS NOT NULL; **unique** `ux_chat_message_one_reply` (reply_to_message_id) WHERE reply_to_message_id IS NOT NULL

### `chat_citation`

- Columns: `id`, `message_id`, `rank`, `document_id`, `version_id`, `chunk_id`, `document_title`, `version_number`, `page_number`, `section`, `score`, `snippet`
- PK: `id`
- FKs: `message_id` → `chat_message`, `document_id` → `knowledge_document`, `version_id` → `knowledge_document_version` — all `ON DELETE RESTRICT` unless marked
- Indexes: `IX_chat_citation_document_id` (document_id); `IX_chat_citation_version_id` (version_id); **unique** `ux_chat_citation_message_rank` (message_id, rank)

### `contract_event`

- Columns: `id`, `contract_id`, `event_type`, `from_status`, `to_status`, `actor_user_id`, `detail`, `created_at`
- PK: `id`
- FKs: `actor_user_id` → `user_account`, `contract_id` → `customer_contract` — all `ON DELETE RESTRICT` unless marked
- Indexes: `IX_contract_event_actor_user_id` (actor_user_id); `ix_contract_event_contract_created_at` (contract_id, created_at)

### `contract_signature_submission`

- Columns: `id`, `contract_id`, `file_id`, `file_sha256`, `official_document_sha256`, `submitted_by_user_id`, `submitted_at`, `outcome`, `reviewed_by_user_id`, `reviewed_at`, `rejection_reason`
- PK: `id`
- FKs: `file_id` → `file_reference`, `reviewed_by_user_id` → `user_account`, `submitted_by_user_id` → `user_account`, `contract_id` → `customer_contract` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_contract_signature_submission_outcome`
- Indexes: `IX_contract_signature_submission_reviewed_by_user_id` (reviewed_by_user_id); `IX_contract_signature_submission_submitted_by_user_id` (submitted_by_user_id); **unique** `ux_contract_signature_submission_file` (file_id); **unique** `ux_contract_signature_submission_one_pending` (contract_id, outcome) WHERE outcome = 'PENDING'

### `customer_contract`

- Columns: `id`, `contract_number`, `version`, `previous_contract_id`, `quotation_id`, `quotation_version`, `survey_request_id`, `customer_user_id`, `sales_user_id`, `status`, `currency`, `total_amount`, `pricing_snapshot`, `customer_snapshot`, `terms`, `canonical_hash`, `official_document_file_id`, `official_document_sha256`, `official_document_confirmed_by_user_id`, `official_document_confirmed_at`, `signed_document_file_id`, `signed_document_sha256`, `signed_submitted_at`, `reviewed_by_user_id`, `reviewed_at`, `rejection_reason`, `issued_at`, `published_at`, `signed_at`, `paid_at`, `created_at`, `updated_at`, `revision`
- PK: `id`
- FKs: `previous_contract_id` → `customer_contract`, `official_document_file_id` → `file_reference`, `signed_document_file_id` → `file_reference`, `customer_user_id` → `user_account`, `official_document_confirmed_by_user_id` → `user_account`, `reviewed_by_user_id` → `user_account`, `sales_user_id` → `user_account`, `quotation_id` → `quotation`, `survey_request_id` → `survey_request` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_customer_contract_amount`, `ck_customer_contract_issued_hash`, `ck_customer_contract_published_document`, `ck_customer_contract_signed_review`, `ck_customer_contract_status`, `ck_customer_contract_version`
- Indexes: `ix_customer_contract_customer_status` (customer_user_id, status); `IX_customer_contract_official_document_confirmed_by_user_id` (official_document_confirmed_by_user_id); `IX_customer_contract_official_document_file_id` (official_document_file_id); `IX_customer_contract_previous_contract_id` (previous_contract_id); `IX_customer_contract_reviewed_by_user_id` (reviewed_by_user_id); `IX_customer_contract_sales_user_id` (sales_user_id); `IX_customer_contract_signed_document_file_id` (signed_document_file_id); `ix_customer_contract_survey_request` (survey_request_id); **unique** `ux_customer_contract_number_version` (contract_number, version); **unique** `ux_customer_contract_one_draft` (quotation_id, status) WHERE status = 'DRAFT'; **unique** `ux_customer_contract_one_live` (quotation_id) WHERE status NOT IN ('DRAFT', 'SUPERSEDED')

### `payment_attempt`

- Columns: `id`, `contract_id`, `customer_user_id`, `provider`, `order_code`, `amount`, `currency`, `description`, `status`, `provider_payment_link_id`, `checkout_url`, `qr_code`, `expires_at`, `provider_reference`, `amount_paid`, `provider_transaction_at`, `paid_at`, `failure_reason`, `last_reconciled_at`, `created_at`, `updated_at`, `revision`
- PK: `id`
- FKs: `contract_id` → `customer_contract`, `customer_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_payment_attempt_amount`, `ck_payment_attempt_order_code`, `ck_payment_attempt_paid_fields`, `ck_payment_attempt_status`
- Indexes: `ix_payment_attempt_contract_created_at` (contract_id, created_at); `IX_payment_attempt_customer_user_id` (customer_user_id); **unique** `ux_payment_attempt_one_paid` (contract_id, status) WHERE status = 'PAID'; **unique** `ux_payment_attempt_one_payable` (contract_id) WHERE status IN ('PENDING', 'PROCESSING'); **unique** `ux_payment_attempt_order_code` (order_code); **unique** `ux_payment_attempt_provider_reference` (provider, provider_reference) WHERE provider_reference IS NOT NULL

### `payment_event`

- Columns: `id`, `payment_attempt_id`, `contract_id`, `event_type`, `from_status`, `to_status`, `source`, `detail`, `created_at`
- PK: `id`
- FKs: `contract_id` → `customer_contract`, `payment_attempt_id` → `payment_attempt` — all `ON DELETE RESTRICT` unless marked
- Indexes: `ix_payment_event_contract_created_at` (contract_id, created_at); `IX_payment_event_payment_attempt_id` (payment_attempt_id)

### `installation_acceptance`

- Columns: `id`, `project_id`, `round`, `requested_by_user_id`, `requested_at`, `completion_report`, `decision`, `decided_by_user_id`, `decided_at`, `reason`
- PK: `id`
- FKs: `decided_by_user_id` → `user_account`, `requested_by_user_id` → `user_account`, `project_id` → `installation_project` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installation_acceptance_decided`, `ck_installation_acceptance_decision`
- Indexes: `IX_installation_acceptance_decided_by_user_id` (decided_by_user_id); `IX_installation_acceptance_requested_by_user_id` (requested_by_user_id); **unique** `ux_installation_acceptance_one_pending` (project_id, decision) WHERE decision = 'PENDING'; **unique** `ux_installation_acceptance_project_round` (project_id, round)

### `installation_checklist_item`

- Columns: `id`, `project_id`, `code`, `title`, `is_required`, `status`, `evidence_file_id`, `note`, `verified_by_user_id`, `verified_at`
- PK: `id`
- FKs: `evidence_file_id` → `file_reference`, `verified_by_user_id` → `user_account`, `project_id` → `installation_project` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installation_checklist_item_evidence`, `ck_installation_checklist_item_status`
- Indexes: `IX_installation_checklist_item_evidence_file_id` (evidence_file_id); `IX_installation_checklist_item_verified_by_user_id` (verified_by_user_id); **unique** `ux_installation_checklist_item_project_code` (project_id, code)

### `installation_event`

- Columns: `id`, `project_id`, `event_type`, `actor_user_id`, `detail`, `created_at`
- PK: `id`
- FKs: `actor_user_id` → `user_account`, `project_id` → `installation_project` — all `ON DELETE RESTRICT` unless marked
- Indexes: `IX_installation_event_actor_user_id` (actor_user_id); `ix_installation_event_project_created_at` (project_id, created_at)

### `installation_milestone`

- Columns: `id`, `project_id`, `sort_order`, `code`, `name`, `description`, `weight_percent`, `is_mandatory`, `status`, `progress_percent`, `notes`, `rework_round`, `started_at`, `completed_at`, `completed_by_user_id`, `revision`
- PK: `id`
- FKs: `completed_by_user_id` → `user_account`, `project_id` → `installation_project` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installation_milestone_completed`, `ck_installation_milestone_numbers`, `ck_installation_milestone_status`
- Indexes: `IX_installation_milestone_completed_by_user_id` (completed_by_user_id); **unique** `ux_installation_milestone_project_code` (project_id, code); **unique** `ux_installation_milestone_project_order` (project_id, sort_order)

### `installation_milestone_evidence`

- Columns: `id`, `milestone_id`, `project_id`, `file_id`, `rework_round`, `caption`, `added_by_user_id`, `added_at`
- PK: `id`
- FKs: `file_id` → `file_reference`, `milestone_id` → `installation_milestone`, `added_by_user_id` → `user_account`, `project_id` → `installation_project` — all `ON DELETE RESTRICT` unless marked
- Indexes: `IX_installation_milestone_evidence_added_by_user_id` (added_by_user_id); `ix_installation_milestone_evidence_milestone_round` (milestone_id, rework_round); `IX_installation_milestone_evidence_project_id` (project_id); **unique** `ux_installation_milestone_evidence_file` (file_id)

### `installation_project`

- Columns: `id`, `contract_id`, `survey_request_id`, `customer_user_id`, `property_site_id`, `manager_user_id`, `status`, `planned_start_date`, `planned_end_date`, `started_at`, `acceptance_requested_at`, `completed_at`, `handover_at`, `acceptance_round`, `created_at`, `updated_at`, `revision`
- PK: `id`
- FKs: `contract_id` → `customer_contract`, `property_site_id` → `property_site`, `customer_user_id` → `user_account`, `manager_user_id` → `user_account`, `survey_request_id` → `survey_request` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installation_project_completed`, `ck_installation_project_dates`, `ck_installation_project_status`
- Indexes: `ix_installation_project_customer_status` (customer_user_id, status); `IX_installation_project_manager_user_id` (manager_user_id); `IX_installation_project_property_site_id` (property_site_id); `IX_installation_project_survey_request_id` (survey_request_id); **unique** `ux_installation_project_contract` (contract_id)

### `installation_shift`

- Columns: `id`, `project_id`, `local_date`, `local_start`, `local_end`, `starts_at`, `ends_at`, `status`, `created_at`
- PK: `id`
- FKs: `project_id` → `installation_project` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installation_shift_range`, `ck_installation_shift_status`
- Indexes: **unique** `ux_installation_shift_project_start` (project_id, starts_at) WHERE status = 'ACTIVE'

### `installation_team_member`

- Columns: `id`, `project_id`, `technician_user_id`, `is_leader`, `assigned_by_user_id`, `assigned_at`, `removed_at`
- PK: `id`
- FKs: `project_id` → `installation_project`, `assigned_by_user_id` → `user_account`, `technician_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installation_team_member_leader_active`
- Indexes: `IX_installation_team_member_assigned_by_user_id` (assigned_by_user_id); `IX_installation_team_member_technician_user_id` (technician_user_id); **unique** `ux_installation_team_member_active` (project_id, technician_user_id) WHERE removed_at IS NULL; **unique** `ux_installation_team_member_one_leader` (project_id, is_leader) WHERE is_leader AND removed_at IS NULL

### `installed_asset`

- Columns: `id`, `project_id`, `asset_type`, `product_id`, `manufacturer`, `model`, `serial_number`, `position_code`, `warranty_months`, `registered_by_user_id`, `registered_at`
- PK: `id`
- FKs: `project_id` → `installation_project`, `product_id` → `product`, `registered_by_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installed_asset_type`, `ck_installed_asset_warranty`
- Indexes: `IX_installed_asset_product_id` (product_id); `ix_installed_asset_project` (project_id); `IX_installed_asset_registered_by_user_id` (registered_by_user_id); **unique** `ux_installed_asset_manufacturer_serial` (manufacturer, serial_number)

### `installation_shift_assignment`

- Columns: `id`, `shift_id`, `project_id`, `technician_user_id`, `starts_at`, `ends_at`, `status`, `created_at`, `released_at`
- PK: `id`
- FKs: `project_id` → `installation_project`, `shift_id` → `installation_shift`, `technician_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_installation_shift_assignment_range`, `ck_installation_shift_assignment_released`, `ck_installation_shift_assignment_status`
- Indexes: `IX_installation_shift_assignment_project_id` (project_id); `ix_installation_shift_assignment_technician_starts_at` (technician_user_id, starts_at); **unique** `ux_installation_shift_assignment_shift_technician` (shift_id, technician_user_id) WHERE status = 'ACTIVE'

### `knowledge_document`

- Columns: `id`, `title`, `category`, `tags`, `status`, `latest_version_id`, `published_version_id`, `created_by_user_id`, `created_at`, `updated_at`, `archived_at`, `revision`
- PK: `id`
- FKs: `created_by_user_id` → `user_account`, `latest_version_id, id` → `knowledge_document_version`, `published_version_id, id` → `knowledge_document_version` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_knowledge_document_published`, `ck_knowledge_document_status`
- Indexes: `IX_knowledge_document_created_by_user_id` (created_by_user_id); `ix_knowledge_document_latest_version` (latest_version_id, id); `ix_knowledge_document_published_version` (published_version_id, id); `ix_knowledge_document_status` (status)

### `knowledge_document_version`

- Columns: `id`, `document_id`, `version_number`, `file_id`, `status`, `page_count`, `chunk_count`, `extracted_characters`, `embedding_model`, `embedding_dimension`, `failure_reason`, `created_by_user_id`, `created_at`, `ingested_at`, `published_at`, `revision`
- PK: `id`
- Alternate keys: `ak_knowledge_document_version_id_document` (id, document_id)
- FKs: `file_id` → `file_reference`, `document_id` → `knowledge_document`, `created_by_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_knowledge_document_version_number`, `ck_knowledge_document_version_status`
- Indexes: `IX_knowledge_document_version_created_by_user_id` (created_by_user_id); **unique** `ux_knowledge_document_version_file` (file_id); **unique** `ux_knowledge_document_version_number` (document_id, version_number); **unique** `ux_knowledge_document_version_one_published` (document_id, status) WHERE status = 'PUBLISHED'

### `knowledge_ingestion_job`

- Columns: `id`, `version_id`, `kind`, `status`, `idempotency_key`, `attempts`, `max_attempts`, `next_attempt_at`, `locked_until`, `last_error`, `created_at`, `updated_at`, `completed_at`
- PK: `id`
- FKs: `version_id` → `knowledge_document_version` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_knowledge_ingestion_job_attempts`, `ck_knowledge_ingestion_job_kind`, `ck_knowledge_ingestion_job_status`
- Indexes: `ix_knowledge_ingestion_job_due` (status, next_attempt_at); `IX_knowledge_ingestion_job_version_id` (version_id); **unique** `ux_knowledge_ingestion_job_idempotency` (idempotency_key)

### `onsite_survey_amendment`

- Columns: `id`, `report_id`, `amendment_number`, `author_user_id`, `reason`, `changes`, `created_at`
- PK: `id`
- FKs: `author_user_id` → `user_account`, `report_id` → `onsite_survey_report` — all `ON DELETE RESTRICT` unless marked
- Indexes: `IX_onsite_survey_amendment_author_user_id` (author_user_id); **unique** `ux_onsite_survey_amendment_report_number` (report_id, amendment_number)

### `onsite_survey_attachment`

- Columns: `id`, `report_id`, `file_id`, `category`, `caption`, `added_by_user_id`, `added_at`, `amendment_id`
- PK: `id`
- FKs: `file_id` → `file_reference`, `amendment_id` → `onsite_survey_amendment`, `added_by_user_id` → `user_account`, `report_id` → `onsite_survey_report` — all `ON DELETE RESTRICT` unless marked
- Indexes: `IX_onsite_survey_attachment_added_by_user_id` (added_by_user_id); `IX_onsite_survey_attachment_amendment_id` (amendment_id); `ix_onsite_survey_attachment_report` (report_id); **unique** `ux_onsite_survey_attachment_file` (file_id)

### `onsite_survey_obstacle`

- Columns: `id`, `report_id`, `sort_order`, `kind`, `description`, `height_m`, `area_m2`
- PK: `id`
- FKs: `report_id` → `onsite_survey_report` (**ON DELETE CASCADE**) — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_onsite_survey_obstacle_sizes`
- Indexes: **unique** `ux_onsite_survey_obstacle_report_order` (report_id, sort_order)

### `onsite_survey_report`

- Columns: `id`, `survey_request_id`, `appointment_id`, `technician_user_id`, `status`, `started_at`, `completed_at`, `roof_type`, `roof_material`, `roof_length_m`, `roof_width_m`, `usable_area_m2`, `tilt_degree`, `azimuth_degree`, `roof_condition`, `electrical_cabinet_notes`, `electrical_connection_notes`, `installation_feasibility`, `proposed_inverter_location`, `proposed_panel_layout_notes`, `measured_latitude`, `measured_longitude`, `notes`, `version`, `created_at`, `updated_at`, `revision`
- PK: `id`
- FKs: `technician_user_id` → `user_account`, `survey_request_id` → `survey_request`, `appointment_id` → `technician_appointment` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_onsite_survey_report_angles`, `ck_onsite_survey_report_completed`, `ck_onsite_survey_report_dimensions`, `ck_onsite_survey_report_location_paired`, `ck_onsite_survey_report_status`
- Indexes: `ix_onsite_survey_report_appointment` (appointment_id); `IX_onsite_survey_report_technician_user_id` (technician_user_id); **unique** `ux_onsite_survey_report_survey_request` (survey_request_id)

### `quotation`

- Columns: `id`, `quote_number`, `survey_request_id`, `onsite_survey_report_id`, `onsite_survey_version`, `version`, `previous_quotation_id`, `status`, `customer_user_id`, `sales_user_id`, `currency`, `equipment_subtotal`, `labor_cost`, `transport_cost`, `other_service_cost`, `subtotal`, `discount_amount`, `tax_rate_percent`, `tax_amount`, `total_amount`, `valid_until`, `terms`, `notes`, `customer_snapshot`, `document_file_id`, `issued_at`, `accepted_at`, `revision_requested_at`, `revision_reason`, `superseded_at`, `created_at`, `updated_at`, `revision`
- PK: `id`
- FKs: `document_file_id` → `file_reference`, `onsite_survey_report_id` → `onsite_survey_report`, `previous_quotation_id` → `quotation`, `survey_request_id` → `survey_request`, `customer_user_id` → `user_account`, `sales_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_quotation_amounts_non_negative`, `ck_quotation_currency`, `ck_quotation_discount_bounded`, `ck_quotation_issued_fields`, `ck_quotation_status`, `ck_quotation_tax_rate`, `ck_quotation_total_consistent`, `ck_quotation_version_positive`
- Indexes: `ix_quotation_customer_status` (customer_user_id, status); `IX_quotation_document_file_id` (document_file_id); `IX_quotation_onsite_survey_report_id` (onsite_survey_report_id); `IX_quotation_previous_quotation_id` (previous_quotation_id); `IX_quotation_sales_user_id` (sales_user_id); **unique** `ux_quotation_number_version` (quote_number, version); **unique** `ux_quotation_one_draft` (survey_request_id) WHERE status = 'DRAFT'; **unique** `ux_quotation_one_live` (survey_request_id, customer_user_id) WHERE status IN ('ISSUED', 'ACCEPTED'); **unique** `ux_quotation_survey_request_version` (survey_request_id, version)

### `quotation_event`

- Columns: `id`, `quotation_id`, `event_type`, `actor_user_id`, `detail`, `created_at`
- PK: `id`
- FKs: `quotation_id` → `quotation`, `actor_user_id` → `user_account` — all `ON DELETE RESTRICT` unless marked
- Indexes: `IX_quotation_event_actor_user_id` (actor_user_id); `ix_quotation_event_quotation_created_at` (quotation_id, created_at)

### `quotation_line`

- Columns: `id`, `quotation_id`, `line_number`, `product_id`, `sku`, `name`, `brand`, `model`, `unit`, `quantity`, `unit_price`, `catalog_unit_price`, `line_total`, `description`
- PK: `id`
- FKs: `product_id` → `product`, `quotation_id` → `quotation` (**ON DELETE CASCADE**) — all `ON DELETE RESTRICT` unless marked
- Checks: `ck_quotation_line_prices`, `ck_quotation_line_quantity`
- Indexes: `IX_quotation_line_product_id` (product_id); **unique** `ux_quotation_line_number` (quotation_id, line_number)

### `knowledge_chunk` (Python-owned, `knowledge_chunk.sql`)

- Columns: `chunk_id` (PK, `{versionId:N}:{index}`), `version_id` → `knowledge_document_version`, `document_id` → `knowledge_document`,
  `chunk_index`, `page_number`, `section`, `content`, `content_sha256`, `embedding vector(1536)`, `embedding_model`, `created_at`
- Indexes: unique `ux_knowledge_chunk_version_index` (version_id, chunk_index), which also serves lookups by version; HNSW
  `ix_knowledge_chunk_embedding_hnsw` (`vector_cosine_ops`). Embeddings: OpenAI `text-embedding-3-small`, 1536 dimensions.
- Least privilege: the RAG service role needs only `SELECT, INSERT, DELETE` on this table.

## 6. How the scripts were verified (disposable databases only)

* `PendingSchemaReviewTests` (1) regenerates the DDL from the EF model and fails if the review script drifted
  (any statement touching a pending table that the generator cannot classify fails the test), and
  (2) independently checks every foreign key with its delete rule, index and check constraint of the EF
  model against the committed script.
* `PostgresInstallationWorkflowConcurrencyTests` and `PostgresKnowledgeBaseConcurrencyTests` create a brand-new local PostgreSQL database (`pgvector/pgvector:pg17`
  container bound to 127.0.0.1), apply existing migrations + the review script, run the race tests, and drop it.
* `tests/test_pgvector.py` (RAG service) does the same for `knowledge_chunk.sql`.
