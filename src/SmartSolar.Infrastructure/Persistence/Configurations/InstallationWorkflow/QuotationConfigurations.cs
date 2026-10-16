using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Infrastructure.Persistence.Configurations.InstallationWorkflow;

public sealed class QuotationConfiguration : IEntityTypeConfiguration<Quotation>
{
    public void Configure(EntityTypeBuilder<Quotation> builder)
    {
        builder.ToTable("quotation", t =>
        {
            t.HasCheckConstraint("ck_quotation_status", $"status IN ({string.Join(", ", QuotationStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_quotation_currency", "currency = 'VND'");
            t.HasCheckConstraint("ck_quotation_amounts_non_negative",
                "equipment_subtotal >= 0 AND labor_cost >= 0 AND transport_cost >= 0 AND other_service_cost >= 0 AND discount_amount >= 0 AND tax_amount >= 0");
            t.HasCheckConstraint("ck_quotation_discount_bounded", "discount_amount <= subtotal");
            t.HasCheckConstraint("ck_quotation_total_consistent", "total_amount = subtotal - discount_amount + tax_amount");
            t.HasCheckConstraint("ck_quotation_tax_rate", "tax_rate_percent BETWEEN 0 AND 20");
            t.HasCheckConstraint("ck_quotation_version_positive", "version >= 1");
            t.HasCheckConstraint("ck_quotation_issued_fields",
                $"status = '{QuotationStatuses.Draft}' OR (issued_at IS NOT NULL AND valid_until IS NOT NULL AND customer_snapshot IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.QuoteNumber).HasColumnName("quote_number").HasMaxLength(30).IsRequired();
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired();
        builder.Property(x => x.OnSiteSurveyReportId).HasColumnName("onsite_survey_report_id").IsRequired();
        builder.Property(x => x.OnSiteSurveyVersion).HasColumnName("onsite_survey_version").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.PreviousQuotationId).HasColumnName("previous_quotation_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.SalesUserId).HasColumnName("sales_user_id").IsRequired();
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        foreach (var (property, column) in new (System.Linq.Expressions.Expression<Func<Quotation, decimal>>, string)[]
                 {
                     (x => x.EquipmentSubtotal, "equipment_subtotal"), (x => x.LaborCost, "labor_cost"), (x => x.TransportCost, "transport_cost"),
                     (x => x.OtherServiceCost, "other_service_cost"), (x => x.Subtotal, "subtotal"), (x => x.DiscountAmount, "discount_amount"),
                     (x => x.TaxAmount, "tax_amount"), (x => x.TotalAmount, "total_amount"),
                 })
            builder.Property(property).HasColumnName(column).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.TaxRatePercent).HasColumnName("tax_rate_percent").HasPrecision(5, 2).IsRequired();
        builder.Property(x => x.ValidUntil).HasColumnName("valid_until");
        builder.Property(x => x.Terms).HasColumnName("terms").HasMaxLength(4000);
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);
        builder.Property(x => x.CustomerSnapshot).HasColumnName("customer_snapshot").HasColumnType("jsonb");
        builder.Property(x => x.DocumentFileId).HasColumnName("document_file_id");
        builder.Property(x => x.IssuedAt).HasColumnName("issued_at");
        builder.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(x => x.RevisionRequestedAt).HasColumnName("revision_requested_at");
        builder.Property(x => x.RevisionReason).HasColumnName("revision_reason").HasMaxLength(2000);
        builder.Property(x => x.SupersededAt).HasColumnName("superseded_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => new { x.QuoteNumber, x.Version }).IsUnique().HasDatabaseName("ux_quotation_number_version");
        builder.HasIndex(x => new { x.SurveyRequestId, x.Version }).IsUnique().HasDatabaseName("ux_quotation_survey_request_version");
        // At most one draft and one live (issued/accepted) offer per survey request.
        builder.HasIndex(x => x.SurveyRequestId).IsUnique().HasFilter($"status = '{QuotationStatuses.Draft}'").HasDatabaseName("ux_quotation_one_draft");
        builder.HasIndex(x => new { x.SurveyRequestId, x.CustomerUserId }).IsUnique()
            .HasFilter($"status IN ('{QuotationStatuses.Issued}', '{QuotationStatuses.Accepted}')").HasDatabaseName("ux_quotation_one_live");
        builder.HasIndex(x => new { x.CustomerUserId, x.Status }).HasDatabaseName("ix_quotation_customer_status");

        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OnSiteSurveyReport>().WithMany().HasForeignKey(x => x.OnSiteSurveyReportId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Quotation>().WithMany().HasForeignKey(x => x.PreviousQuotationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.SalesUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.DocumentFileId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QuotationLineConfiguration : IEntityTypeConfiguration<QuotationLine>
{
    public void Configure(EntityTypeBuilder<QuotationLine> builder)
    {
        builder.ToTable("quotation_line", t =>
        {
            t.HasCheckConstraint("ck_quotation_line_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_quotation_line_prices", "unit_price >= 0 AND line_total >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.QuotationId).HasColumnName("quotation_id").IsRequired();
        builder.Property(x => x.LineNumber).HasColumnName("line_number").IsRequired();
        builder.Property(x => x.ProductId).HasColumnName("product_id");
        builder.Property(x => x.Sku).HasColumnName("sku").HasMaxLength(50);
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Brand).HasColumnName("brand").HasMaxLength(100);
        builder.Property(x => x.Model).HasColumnName("model").HasMaxLength(100);
        builder.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(12, 3).IsRequired();
        builder.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.CatalogUnitPrice).HasColumnName("catalog_unit_price").HasPrecision(18, 2);
        builder.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        builder.HasIndex(x => new { x.QuotationId, x.LineNumber }).IsUnique().HasDatabaseName("ux_quotation_line_number");
        builder.HasOne<Quotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class QuotationEventConfiguration : IEntityTypeConfiguration<QuotationEvent>
{
    public void Configure(EntityTypeBuilder<QuotationEvent> builder)
    {
        builder.ToTable("quotation_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.QuotationId).HasColumnName("quotation_id").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.QuotationId, x.CreatedAt }).HasDatabaseName("ix_quotation_event_quotation_created_at");
        builder.HasOne<Quotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
