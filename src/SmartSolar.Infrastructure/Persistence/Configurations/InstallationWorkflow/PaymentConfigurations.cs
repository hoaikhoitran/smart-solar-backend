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
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
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
        builder.Property(x => x.ContractId).HasColumnName("contract_id").IsRequired();
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
