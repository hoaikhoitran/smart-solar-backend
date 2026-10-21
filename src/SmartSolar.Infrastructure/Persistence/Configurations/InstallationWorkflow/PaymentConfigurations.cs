using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.Persistence.Configurations.InstallationWorkflow;

public sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempt", t =>
        {
            t.HasCheckConstraint("ck_payment_attempt_status", $"status IN ({string.Join(", ", PaymentStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_payment_attempt_amount", "amount > 0 AND currency = 'VND'");
            t.HasCheckConstraint("ck_payment_attempt_order_code", "order_code > 0 AND order_code < 9007199254740991");
            t.HasCheckConstraint("ck_payment_attempt_paid_fields", $"status <> '{PaymentStatuses.Paid}' OR (paid_at IS NOT NULL AND amount_paid = amount)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        // Null for an after-sales-only checkout (its items say what it pays).
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired(false);
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(20).IsRequired();
        builder.Property(x => x.OrderCode).HasColumnName("order_code").IsRequired();
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(25).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.ProviderPaymentLinkId).HasColumnName("provider_payment_link_id").HasMaxLength(100);
        builder.Property(x => x.CheckoutUrl).HasColumnName("checkout_url").HasMaxLength(500);
        builder.Property(x => x.QrCode).HasColumnName("qr_code").HasMaxLength(1000);
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.ProviderReference).HasColumnName("provider_reference").HasMaxLength(100);
        builder.Property(x => x.AmountPaid).HasColumnName("amount_paid").HasPrecision(18, 2);
        builder.Property(x => x.ProviderTransactionAt).HasColumnName("provider_transaction_at");
        builder.Property(x => x.PaidAt).HasColumnName("paid_at");
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(300);
        builder.Property(x => x.LastReconciledAt).HasColumnName("last_reconciled_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.OrderCode).IsUnique().HasDatabaseName("ux_payment_attempt_order_code");
        builder.HasIndex(x => new { x.Provider, x.ProviderReference }).IsUnique().HasFilter("provider_reference IS NOT NULL").HasDatabaseName("ux_payment_attempt_provider_reference");
        // Never two payable links, never two successful payments for one contract.
        builder.HasIndex(x => x.ContractId).IsUnique()
            .HasFilter($"status IN ('{PaymentStatuses.Pending}', '{PaymentStatuses.Processing}')").HasDatabaseName("ux_payment_attempt_one_payable");
        builder.HasIndex(x => new { x.ContractId, x.Status }).IsUnique()
            .HasFilter($"status = '{PaymentStatuses.Paid}'").HasDatabaseName("ux_payment_attempt_one_paid");
        builder.HasIndex(x => new { x.ContractId, x.CreatedAt }).HasDatabaseName("ix_payment_attempt_contract_created_at");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PaymentWebhookEventConfiguration : IEntityTypeConfiguration<PaymentWebhookEvent>
{
    public void Configure(EntityTypeBuilder<PaymentWebhookEvent> builder)
    {
        builder.ToTable("payment_webhook_event", t => t.HasCheckConstraint("ck_payment_webhook_event_outcome",
            $"outcome IN ('{WebhookOutcomes.Applied}', '{WebhookOutcomes.Duplicate}', '{WebhookOutcomes.Ignored}', '{WebhookOutcomes.Rejected}')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(20).IsRequired();
        builder.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.OrderCode).HasColumnName("order_code");
        builder.Property(x => x.ProviderReference).HasColumnName("provider_reference").HasMaxLength(100);
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2);
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(10);
        builder.Property(x => x.SignatureValid).HasColumnName("signature_valid").IsRequired();
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(300);
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.HasIndex(x => new { x.Provider, x.PayloadSha256 }).IsUnique().HasDatabaseName("ux_payment_webhook_event_payload");
        builder.HasIndex(x => x.OrderCode).HasDatabaseName("ix_payment_webhook_event_order_code");
    }
}

public sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        builder.ToTable("payment_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PaymentAttemptId).HasColumnName("payment_attempt_id").IsRequired();
        // Null for an after-sales-only checkout (its items say what it pays).
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired(false);
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(30);
        builder.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(30);
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(300);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.ContractId, x.CreatedAt }).HasDatabaseName("ix_payment_event_contract_created_at");
        builder.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Allocation lines of a checkout: exactly one target per line, amounts are whole VND.</summary>
public sealed class PaymentAttemptItemConfiguration : IEntityTypeConfiguration<PaymentAttemptItem>
{
    public void Configure(EntityTypeBuilder<PaymentAttemptItem> builder)
    {
        builder.ToTable("payment_attempt_item", t =>
        {
            t.HasCheckConstraint("ck_payment_attempt_item_type", $"item_type IN ({string.Join(", ", PaymentItemTypes.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_payment_attempt_item_status", $"status IN ({string.Join(", ", PaymentItemStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_payment_attempt_item_amount", "amount > 0");
            t.HasCheckConstraint("ck_payment_attempt_item_target",
                "(CASE WHEN contract_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN subscription_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN quotation_id IS NULL THEN 0 ELSE 1 END) = 1");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PaymentAttemptId).HasColumnName("payment_attempt_id").IsRequired();
        builder.Property(x => x.ItemType).HasColumnName("item_type").HasMaxLength(30).IsRequired();
        builder.Property(x => x.ContractId).HasColumnName("contract_id");
        builder.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        builder.Property(x => x.QuotationId).HasColumnName("quotation_id");
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(300);
        builder.HasIndex(x => x.PaymentAttemptId).HasDatabaseName("ix_payment_attempt_item_attempt");
        builder.HasIndex(x => x.SubscriptionId).HasDatabaseName("ix_payment_attempt_item_subscription");
        builder.HasIndex(x => x.QuotationId).HasDatabaseName("ix_payment_attempt_item_quotation");
        // A checkout lists a package or quotation at most once, and a package or quotation is settled by at most one verified payment.
        builder.HasIndex(x => new { x.PaymentAttemptId, x.ContractId }, "ux_payment_attempt_item_attempt_contract").IsUnique()
            .HasFilter("contract_id IS NOT NULL");
        builder.HasIndex(x => new { x.PaymentAttemptId, x.SubscriptionId }, "ux_payment_attempt_item_attempt_subscription").IsUnique()
            .HasFilter("subscription_id IS NOT NULL");
        builder.HasIndex(x => new { x.PaymentAttemptId, x.QuotationId }, "ux_payment_attempt_item_attempt_quotation").IsUnique()
            .HasFilter("quotation_id IS NOT NULL");
        builder.HasIndex(x => x.SubscriptionId, "ux_payment_attempt_item_settled_subscription").IsUnique()
            .HasFilter($"subscription_id IS NOT NULL AND status = '{PaymentItemStatuses.Settled}'");
        builder.HasIndex(x => x.QuotationId, "ux_payment_attempt_item_settled_quotation").IsUnique()
            .HasFilter($"quotation_id IS NOT NULL AND status = '{PaymentItemStatuses.Settled}'");
        builder.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payment_attempt_item_attempt");
        builder.HasOne<CustomerContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payment_attempt_item_contract");
        builder.HasOne<SmartSolar.Modules.AfterSales.ServiceSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payment_attempt_item_subscription");
        builder.HasOne<SmartSolar.Modules.AfterSales.ServiceQuotation>().WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payment_attempt_item_quotation");
    }
}
