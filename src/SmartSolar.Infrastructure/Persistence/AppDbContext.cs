using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Entities;
using SimulationEntity = SmartSolar.Modules.SolarSimulation.Entities.SolarSimulation;

namespace SmartSolar.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<AuthActionToken> AuthActionTokens => Set<AuthActionToken>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<PropertySite> PropertySites => Set<PropertySite>();

    public DbSet<PreSurvey> PreSurveys => Set<PreSurvey>();

    public DbSet<Product> Products => Set<Product>();
    public DbSet<SurveyRequest> SurveyRequests
    => Set<SurveyRequest>();

    public DbSet<SimulationEntity> SolarSimulations => Set<SimulationEntity>();

    public DbSet<TechnicianProfile> TechnicianProfiles => Set<TechnicianProfile>();

    public DbSet<TechnicianUnavailablePeriod> TechnicianUnavailablePeriods => Set<TechnicianUnavailablePeriod>();

    public DbSet<TechnicianAppointment> TechnicianAppointments => Set<TechnicianAppointment>();

    public DbSet<TechnicianTravelEstimate> TechnicianTravelEstimates => Set<TechnicianTravelEstimate>();

    public DbSet<TechnicianWorkingPeriod> TechnicianWorkingPeriods => Set<TechnicianWorkingPeriod>();

    public DbSet<TechnicianScheduleRecalculation> TechnicianScheduleRecalculations => Set<TechnicianScheduleRecalculation>();

    public DbSet<TechnicianAppointmentEvent> TechnicianAppointmentEvents => Set<TechnicianAppointmentEvent>();

    public DbSet<SchedulingNotification> SchedulingNotifications => Set<SchedulingNotification>();

    public DbSet<UserAccountAuditEntry> UserAccountAuditEntries => Set<UserAccountAuditEntry>();

    // ---- Installation Flow / knowledge base (tables pending a manual, approved migration) ----

    public DbSet<SmartSolar.Modules.Files.StoredFile> StoredFiles => Set<SmartSolar.Modules.Files.StoredFile>();

    public DbSet<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyReport> OnSiteSurveyReports => Set<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyReport>();

    public DbSet<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyObstacle> OnSiteSurveyObstacles => Set<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyObstacle>();

    public DbSet<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyAttachment> OnSiteSurveyAttachments => Set<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyAttachment>();

    public DbSet<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyAmendment> OnSiteSurveyAmendments => Set<SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyAmendment>();

    public DbSet<SmartSolar.Modules.Quotations.Quotation> Quotations => Set<SmartSolar.Modules.Quotations.Quotation>();

    public DbSet<SmartSolar.Modules.Quotations.QuotationLine> QuotationLines => Set<SmartSolar.Modules.Quotations.QuotationLine>();

    public DbSet<SmartSolar.Modules.Quotations.QuotationEvent> QuotationEvents => Set<SmartSolar.Modules.Quotations.QuotationEvent>();

    public DbSet<SmartSolar.Modules.CustomerContracts.CustomerContract> CustomerContracts => Set<SmartSolar.Modules.CustomerContracts.CustomerContract>();

    public DbSet<SmartSolar.Modules.CustomerContracts.ContractSignatureSubmission> ContractSignatureSubmissions => Set<SmartSolar.Modules.CustomerContracts.ContractSignatureSubmission>();

    public DbSet<SmartSolar.Modules.CustomerContracts.ContractEvent> ContractEvents => Set<SmartSolar.Modules.CustomerContracts.ContractEvent>();

    public DbSet<SmartSolar.Modules.Payments.PaymentAttempt> PaymentAttempts => Set<SmartSolar.Modules.Payments.PaymentAttempt>();

    public DbSet<SmartSolar.Modules.Payments.PaymentWebhookEvent> PaymentWebhookEvents => Set<SmartSolar.Modules.Payments.PaymentWebhookEvent>();

    public DbSet<SmartSolar.Modules.Payments.PaymentEvent> PaymentEvents => Set<SmartSolar.Modules.Payments.PaymentEvent>();

    public DbSet<SmartSolar.Modules.Installations.InstallationProject> InstallationProjects => Set<SmartSolar.Modules.Installations.InstallationProject>();

    public DbSet<SmartSolar.Modules.Installations.InstallationTeamMember> InstallationTeamMembers => Set<SmartSolar.Modules.Installations.InstallationTeamMember>();

    public DbSet<SmartSolar.Modules.Installations.InstallationShift> InstallationShifts => Set<SmartSolar.Modules.Installations.InstallationShift>();

    public DbSet<SmartSolar.Modules.Installations.InstallationShiftAssignment> InstallationShiftAssignments => Set<SmartSolar.Modules.Installations.InstallationShiftAssignment>();

    public DbSet<SmartSolar.Modules.Installations.InstallationMilestone> InstallationMilestones => Set<SmartSolar.Modules.Installations.InstallationMilestone>();

    public DbSet<SmartSolar.Modules.Installations.MilestoneEvidence> MilestoneEvidence => Set<SmartSolar.Modules.Installations.MilestoneEvidence>();

    public DbSet<SmartSolar.Modules.Installations.InstallationAcceptance> InstallationAcceptances => Set<SmartSolar.Modules.Installations.InstallationAcceptance>();

    public DbSet<SmartSolar.Modules.Installations.InstalledAsset> InstalledAssets => Set<SmartSolar.Modules.Installations.InstalledAsset>();

    public DbSet<SmartSolar.Modules.Installations.InstallationChecklistItem> InstallationChecklistItems => Set<SmartSolar.Modules.Installations.InstallationChecklistItem>();

    public DbSet<SmartSolar.Modules.Installations.InstallationEvent> InstallationEvents => Set<SmartSolar.Modules.Installations.InstallationEvent>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.KnowledgeDocument> KnowledgeDocuments => Set<SmartSolar.Modules.KnowledgeBase.KnowledgeDocument>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.KnowledgeDocumentVersion> KnowledgeDocumentVersions => Set<SmartSolar.Modules.KnowledgeBase.KnowledgeDocumentVersion>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.KnowledgeIngestionJob> KnowledgeIngestionJobs => Set<SmartSolar.Modules.KnowledgeBase.KnowledgeIngestionJob>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.ChatSession> ChatSessions => Set<SmartSolar.Modules.KnowledgeBase.ChatSession>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.ChatMessage> ChatMessages => Set<SmartSolar.Modules.KnowledgeBase.ChatMessage>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.ChatCitation> ChatCitations => Set<SmartSolar.Modules.KnowledgeBase.ChatCitation>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.RagUsageDaily> RagUsageDaily => Set<SmartSolar.Modules.KnowledgeBase.RagUsageDaily>();

    public DbSet<SmartSolar.Modules.KnowledgeBase.RagUsageReservation> RagUsageReservations => Set<SmartSolar.Modules.KnowledgeBase.RagUsageReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AppDbContext).Assembly);
    }
}
