using SmartSolar.Modules.CustomerContracts;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>Pure 30/70 financial rules: split, rounding, invariants, due time and its boundary (UTC stored, Asia/Ho_Chi_Minh business time).</summary>
public sealed class PaymentPlanCalculatorTests
{
    [Theory]
    [InlineData(18_500_000, 5_550_000, 12_950_000)]
    [InlineData(1_000_005, 300_002, 700_003)]      // 300,001.5 → midpoint away from zero
    [InlineData(1_000_001, 300_000, 700_001)]      // 300,000.3 → down
    [InlineData(10, 3, 7)]
    [InlineData(3, 1, 2)]                           // 0.9 → 1
    [InlineData(2, 1, 1)]                           // 0.6 → 1
    public void FIN_split_rounds_the_deposit_to_whole_VND_and_the_balance_takes_the_rest(long total, long deposit, long balance)
    {
        var split = PaymentPlanCalculator.Split(total)!;
        Assert.Equal((deposit, balance), ((long)split.Deposit, (long)split.Balance));
        Assert.Equal(total, (long)(split.Deposit + split.Balance));
        Assert.Equal(30m, split.DepositPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]                                 // 0.3 → 0: no zero installment
    [InlineData(-100)]
    [InlineData(2.5)]                               // not whole VND
    public void FIN_totals_that_cannot_be_split_are_refused(double total)
        => Assert.Null(PaymentPlanCalculator.Split((decimal)total));

    [Fact]
    public void FIN_deposit_plus_balance_equals_total_for_every_amount_and_both_are_positive()
    {
        var random = new Random(20261119);
        for (var n = 0; n < 20_000; n++)
        {
            var total = (decimal)random.NextInt64(2, 50_000_000_000);
            var split = PaymentPlanCalculator.Split(total)!;
            Assert.Equal(total, split.Deposit + split.Balance);
            Assert.True(split.Deposit > 0 && split.Balance > 0 && decimal.Truncate(split.Deposit) == split.Deposit);
            Assert.True(Math.Abs(split.Deposit - total * 0.3m) <= 0.5m);
        }
    }

    [Fact]
    public void DUE_balance_is_due_72_hours_after_acceptance_in_UTC_and_the_boundary_is_on_time()
    {
        var options = new PaymentPlanOptions();
        // 23:30 in Ho Chi Minh City (UTC+7) on 19/11 → due 23:30 local on 22/11.
        var acceptedLocal = new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.FromHours(7));
        var due = PaymentPlanCalculator.BalanceDueAt(acceptedLocal, options);
        Assert.Equal(TimeSpan.Zero, due.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 16, 30, 0, TimeSpan.Zero), due);
        Assert.Equal(new DateTime(2026, 11, 22, 23, 30, 0), TimeZoneInfo.ConvertTime(due, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")).DateTime);

        var balance = new ContractInstallment { Kind = InstallmentKinds.Balance, Status = InstallmentStatuses.Due, DueAt = due, Amount = 1 };
        Assert.False(PaymentPlanCalculator.IsOverdue(balance, due));                          // exactly at the deadline: still on time
        Assert.False(PaymentPlanCalculator.IsOverdue(balance, due.AddTicks(-1)));
        Assert.True(PaymentPlanCalculator.IsOverdue(balance, due.AddTicks(1)));
        Assert.True(PaymentPlanCalculator.IsOverdue(balance, due.AddSeconds(1).ToOffset(TimeSpan.FromHours(7))));   // offset-independent
        Assert.False(PaymentPlanCalculator.IsLate(due, due));
        Assert.True(PaymentPlanCalculator.IsLate(due, due.AddSeconds(1)));
    }

    [Fact]
    public void DUE_only_a_DUE_installment_can_be_overdue_and_the_deadline_never_exceeds_three_days()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-10);
        foreach (var status in new[] { InstallmentStatuses.Pending, InstallmentStatuses.Paid, InstallmentStatuses.Cancelled })
            Assert.False(PaymentPlanCalculator.IsOverdue(new ContractInstallment { Status = status, DueAt = past }, DateTimeOffset.UtcNow));
        Assert.False(PaymentPlanCalculator.IsOverdue(new ContractInstallment { Status = InstallmentStatuses.Due, DueAt = null }, DateTimeOffset.UtcNow));
        Assert.Equal(72, new PaymentPlanOptions { BalanceDueHours = 500 }.EffectiveBalanceDueHours);
        Assert.Equal(24, new PaymentPlanOptions { BalanceDueHours = 24 }.EffectiveBalanceDueHours);
        var accepted = new DateTimeOffset(2026, 11, 19, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(accepted.AddHours(72), PaymentPlanCalculator.BalanceDueAt(accepted, new PaymentPlanOptions { BalanceDueHours = 1000 }));
    }

    [Fact]
    public void FIN_summary_counts_only_paid_installments_and_outstanding_is_never_negative()
    {
        var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        var contractId = Guid.NewGuid();
        var installments = PaymentPlanCalculator.CreateInstallments(contractId, PaymentPlanCalculator.Split(10_000_000)!, now).ToList();
        Assert.Equal((InstallmentStatuses.Due, InstallmentStatuses.Pending), (installments[0].Status, installments[1].Status));

        var fresh = PaymentPlanCalculator.Summarize(10_000_000, installments, now);
        Assert.Equal((0m, 10_000_000m, false, false, false), (fresh.Paid, fresh.Outstanding, fresh.DepositPaid, fresh.FullyPaid, fresh.Overdue));

        installments[0].Status = InstallmentStatuses.Paid;
        var afterDeposit = PaymentPlanCalculator.Summarize(10_000_000, installments, now);
        Assert.Equal((3_000_000m, 7_000_000m, true, false), (afterDeposit.Paid, afterDeposit.Outstanding, afterDeposit.DepositPaid, afterDeposit.FullyPaid));

        installments[1].Status = InstallmentStatuses.Due;
        installments[1].DueAt = now.AddHours(-1);
        var overdue = PaymentPlanCalculator.Summarize(10_000_000, installments, now);
        Assert.True(overdue.Overdue);
        Assert.Equal(now.AddHours(-1), overdue.NextDueAt);

        installments[1].Status = InstallmentStatuses.Paid;
        var paid = PaymentPlanCalculator.Summarize(10_000_000, installments, now);
        Assert.Equal((10_000_000m, 0m, true, false), (paid.Paid, paid.Outstanding, paid.FullyPaid, paid.Overdue));
        // A total below the paid sum (never possible through the API) still never shows a negative outstanding amount.
        Assert.Equal(0m, PaymentPlanCalculator.Summarize(1m, installments, now).Outstanding);
    }

    [Fact]
    public void LEGAL_generated_clauses_state_amounts_and_deadline_and_flag_legal_review_without_deciding_anything()
    {
        var clauses = PaymentPlanCalculator.Clauses(PaymentPlanCalculator.Split(18_500_000)!, new PaymentPlanOptions());
        Assert.Contains("5,550,000", clauses.Replace('.', ','));
        Assert.Contains("12,950,000", clauses.Replace('.', ','));
        Assert.Contains("72 giờ", clauses);
        Assert.Contains("PENDING_LEGAL_REVIEW", clauses);
        Assert.Contains("Điều 328", clauses);
        Assert.Contains("không tự động thu hồi", clauses);
    }
}
