namespace SmartSolar.Modules.CustomerContracts;

/// <summary>Configuration of the 30/70 payment plan (section <c>PaymentPlan</c>).</summary>
public sealed class PaymentPlanOptions
{
    public const string SectionName = "PaymentPlan";

    /// <summary>Hours between the customer's acceptance and the balance deadline. Business rule: at most 3 days (values above 72 are capped).</summary>
    public int BalanceDueHours { get; set; } = 72;

    /// <summary>
    /// Version label of the payment / ownership clauses generated into new contracts. The default says the text still needs legal approval; set the
    /// approved version label once Legal signs the clauses off (see guides/payment-30-70/LEGAL_REVIEW_CHECKLIST.md).
    /// </summary>
    public string TermsVersion { get; set; } = "PAYMENT_30_70_DRAFT_PENDING_LEGAL_REVIEW";

    /// <summary>Business timezone used for the dates printed in the clauses and the local views (UTC is stored).</summary>
    public string TimeZoneId { get; set; } = "Asia/Ho_Chi_Minh";

    /// <summary>Whether a photographed page may be a PDF (scanner apps) besides JPEG / PNG / WebP photos.</summary>
    public bool AllowPdfSignedPages { get; set; } = true;

    public int EffectiveBalanceDueHours => Math.Clamp(BalanceDueHours, 1, PaymentPlanCalculator.MaxBalanceDueHours);
}

/// <summary>The two amounts of a payment-plan contract.</summary>
public sealed record InstallmentSplit(decimal Total, decimal DepositPercent, decimal Deposit, decimal Balance);

/// <summary>Figures of one contract computed from its installments (never from a single "is paid" flag).</summary>
public sealed record ContractFinancialSummary(decimal Total, decimal Paid, decimal Outstanding, bool DepositPaid, bool FullyPaid, bool Overdue,
    DateTimeOffset? NextDueAt);

/// <summary>
/// Pure financial rules of the 30/70 plan (unit-tested; no I/O). Money is <see cref="decimal"/> in whole VND:
/// <list type="bullet">
/// <item>deposit = round(total × 30%) to whole VND, midpoint away from zero; balance = total − deposit (so deposit + balance = total exactly);</item>
/// <item>balance due = acceptance time (UTC) + <see cref="PaymentPlanOptions.EffectiveBalanceDueHours"/> (≤ 72 h);</item>
/// <item>overdue ⇔ the installment is DUE and now is strictly after its due time (exactly at the due time is still on time);</item>
/// <item>paid = Σ amounts of PAID installments; outstanding = total − paid (never negative; money beyond the plan is never applied here).</item>
/// </list>
/// </summary>
public static class PaymentPlanCalculator
{
    public const decimal DepositPercent = 30m;
    public const int MaxBalanceDueHours = 72;

    /// <summary>Null when the total cannot be split (not a whole VND amount ≥ 2).</summary>
    public static InstallmentSplit? Split(decimal total)
    {
        if (total < 2 || decimal.Truncate(total) != total) return null;
        var deposit = Math.Round(total * DepositPercent / 100m, 0, MidpointRounding.AwayFromZero);
        var balance = total - deposit;
        if (deposit <= 0 || balance <= 0) return null;
        return new InstallmentSplit(total, DepositPercent, deposit, balance);
    }

    public static DateTimeOffset BalanceDueAt(DateTimeOffset acceptedAt, PaymentPlanOptions options)
        => acceptedAt.ToUniversalTime().AddHours(options.EffectiveBalanceDueHours);

    public static bool IsOverdue(ContractInstallment installment, DateTimeOffset now)
        => installment.Status == InstallmentStatuses.Due && installment.DueAt is { } due && now.ToUniversalTime() > due.ToUniversalTime();

    public static bool IsLate(DateTimeOffset? dueAt, DateTimeOffset paidAt) => dueAt is { } due && paidAt.ToUniversalTime() > due.ToUniversalTime();

    public static ContractFinancialSummary Summarize(decimal total, IReadOnlyCollection<ContractInstallment> installments, DateTimeOffset now)
    {
        var paid = installments.Where(i => i.Status == InstallmentStatuses.Paid).Sum(i => i.Amount);
        var outstanding = Math.Max(0m, total - paid);
        var deposit = installments.FirstOrDefault(i => i.Kind == InstallmentKinds.Deposit);
        var nextDue = installments.Where(i => i.Status == InstallmentStatuses.Due).OrderBy(i => i.Sequence).Select(i => i.DueAt).FirstOrDefault();
        return new ContractFinancialSummary(total, paid, outstanding, deposit?.Status == InstallmentStatuses.Paid,
            installments.Count > 0 && installments.All(i => i.Status == InstallmentStatuses.Paid), installments.Any(i => IsOverdue(i, now)), nextDue);
    }

    /// <summary>The two installments of a new contract: the deposit is payable at once, the balance waits for the customer's acceptance.</summary>
    public static IReadOnlyList<ContractInstallment> CreateInstallments(Guid contractId, InstallmentSplit split, DateTimeOffset now) =>
    [
        new()
        {
            Id = Guid.NewGuid(), ContractId = contractId, Sequence = 1, Kind = InstallmentKinds.Deposit, Percent = split.DepositPercent, Amount = split.Deposit,
            Status = InstallmentStatuses.Due, OpenedAt = now, CreatedAt = now, UpdatedAt = now,
        },
        new()
        {
            Id = Guid.NewGuid(), ContractId = contractId, Sequence = 2, Kind = InstallmentKinds.Balance, Percent = 100m - split.DepositPercent, Amount = split.Balance,
            Status = InstallmentStatuses.Pending, CreatedAt = now, UpdatedAt = now,
        },
    ];

    /// <summary>
    /// Payment and ownership clauses generated into the contract text (business wording; marked with the terms version so Legal can approve or
    /// replace it). They state facts of this contract only and decide nothing legal (no forfeiture, penalty or repossession is stated).
    /// </summary>
    public static string Clauses(InstallmentSplit split, PaymentPlanOptions options) =>
        $"""
        ĐIỀU KHOẢN THANH TOÁN ({options.TermsVersion})
        1. Tổng giá trị hợp đồng: {split.Total:N0} VND, thanh toán 2 đợt.
        2. Đợt 1 ({split.DepositPercent:0}%): {split.Deposit:N0} VND, thanh toán trước khi ký hợp đồng giấy. Khoản này là khoản thanh toán trước một phần giá trị hợp đồng; tính chất pháp lý (có hay không phải là đặt cọc theo Điều 328 BLDS 2015) theo kết luận của bộ phận pháp chế.
        3. Đợt 2 ({100m - split.DepositPercent:0}%): {split.Balance:N0} VND, thanh toán trong vòng {options.EffectiveBalanceDueHours} giờ kể từ thời điểm khách hàng nghiệm thu công trình trên hệ thống.
        4. Hình thức: chuyển khoản qua mã QR PayOS hoặc tiền mặt có phiếu thu của SmartSolar. Nhân viên kỹ thuật không được nhận xác nhận thanh toán thay công ty.
        ĐIỀU KIỆN NGHIỆM THU
        5. Khách hàng nghiệm thu hoặc yêu cầu sửa chữa (kèm lý do) trên ứng dụng sau khi kỹ thuật viên báo hoàn thành; kỹ thuật viên không nghiệm thu thay khách hàng.
        BẢO LƯU QUYỀN SỞ HỮU
        6. Thiết bị thuộc quyền sở hữu của SmartSolar cho đến khi khách hàng thanh toán đủ hai đợt (bảo lưu quyền sở hữu theo Điều 331–332 BLDS 2015 — nội dung chờ pháp chế phê duyệt). Việc chậm thanh toán được xử lý theo thỏa thuận và quy định pháp luật; hệ thống không tự động thu hồi tài sản hay khấu trừ khoản đã thanh toán.
        """;
}
