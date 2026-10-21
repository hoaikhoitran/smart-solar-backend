using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartSolar.Infrastructure.Persistence;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Facts about the schema used by the migration guards. The InstallationWorkflowAndKnowledgeBase migration is applied to
/// Supabase Dev and therefore immutable: its table list and manual SQL are frozen here. The current EF model (PostgreSQL
/// provider, never connected) is what later migrations must produce.
/// </summary>
public static class PendingSchemaScript
{
    /// <summary>The 32 tables created by 20261013182132_InstallationWorkflowAndKnowledgeBase, exactly as applied.</summary>
    public static readonly IReadOnlyList<string> InstallationWorkflowMigrationTables =
    [
        "chat_citation", "chat_message", "chat_session", "contract_event", "contract_signature_submission", "customer_contract",
        "file_reference", "installation_acceptance", "installation_checklist_item", "installation_event", "installation_milestone",
        "installation_milestone_evidence", "installation_project", "installation_shift", "installation_shift_assignment",
        "installation_team_member", "installed_asset", "knowledge_document", "knowledge_document_version", "knowledge_ingestion_job",
        "onsite_survey_amendment", "onsite_survey_attachment", "onsite_survey_obstacle", "onsite_survey_report", "payment_attempt",
        "payment_event", "payment_webhook_event", "quotation", "quotation_event", "quotation_line", "rag_usage_daily", "rag_usage_reservation",
    ];

    /// <summary>Manual SQL of the InstallationWorkflowAndKnowledgeBase migration (frozen, as applied).</summary>
    public const string Manual = """
        -- Installation crews: one technician can never be in two overlapping ACTIVE installation shifts.
        -- (Survey appointments are serialised against installation planning through the technician_profile row lock.)
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        ALTER TABLE installation_shift_assignment
            ADD CONSTRAINT ex_installation_shift_assignment_no_overlap
            EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
            WHERE (status = 'ACTIVE');
        """;

    /// <summary>
    /// Tables that are no longer in the EF model but intentionally stay in the database (no destructive migration):
    /// they are only dropped by the approval-gated cleanup draft.
    /// </summary>
    public static readonly IReadOnlyList<string> RetainedObsoleteTables =
    [
        "installation_milestone", "installation_milestone_evidence", "installation_shift", "installation_shift_assignment",
        "installation_team_member", "solar_simulation", "technician_schedule_recalculation", "technician_travel_estimate",
    ];

    /// <summary>
    /// Tables of the replaced 3-level service-area model, kept (empty) by the non-destructive round-2 migration and no longer
    /// in the EF model; dropped only by an approved cleanup.
    /// </summary>
    public static readonly IReadOnlyList<string> RetainedServiceAreaTables = ["service_area", "technician_service_area"];

    /// <summary>
    /// Legacy columns kept on tables that ARE in the model (no longer mapped, all NULL in Supabase Dev): their foreign keys to
    /// the retained service_area table are expected in the catalog. Format "table.column".
    /// </summary>
    public static readonly IReadOnlyList<string> RetainedLegacyForeignKeyColumns =
        ["technician_appointment.service_area_id", "installation_project.service_area_id"];

    /// <summary>Every entity type of the current design-time model (keeps check constraints and other annotations).</summary>
    public static IReadOnlyList<IEntityType> ModelEntityTypes()
    {
        using var db = NewModelContext();
        return db.GetService<IDesignTimeModel>().Model.GetEntityTypes().Where(e => e.GetTableName() is not null).ToList();
    }

    public static IReadOnlyList<string> ModelTables()
        => ModelEntityTypes().Select(e => e.GetTableName()!).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();

    private static AppDbContext NewModelContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
}
