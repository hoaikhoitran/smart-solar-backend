# Legal review checklist — 30/70 payment, paper contract, ownership retention (2026-11-19)

The backend implements **business states only**. Nothing below is a legal conclusion; each item needs a decision by the company's legal
counsel before the generated clauses are treated as approved. Until then every new contract carries `terms_version =
PAYMENT_30_70_DRAFT_PENDING_LEGAL_REVIEW` and the contract view exposes `paymentPlan.termsPendingLegalReview = true`.

| # | Topic | Legal basis to check | What the system does today | Decision needed |
|---|---|---|---|---|
| 1 | Nature of the 30% | BLDS 2015 Art. 328 (đặt cọc) | Called "khoản thanh toán trước một phần"; the clause says its legal nature follows Legal's conclusion. No forfeiture logic exists. | Is it a deposit (đặt cọc) with Art. 328 effects, or a prepayment? Wording of the clause. |
| 2 | Ownership retention until fully paid | BLDS 2015 Art. 331–332 (bảo lưu quyền sở hữu); registration requirements | Business status `OWNERSHIP_RETAINED` → `OWNERSHIP_TRANSFER_ELIGIBLE` when both installments are paid. No registration, no repossession. | Is retention valid for installed equipment attached to a building? Is registration needed? Clause text. |
| 3 | Balance deadline (≤ 72 h after acceptance) | Commercial practice; Consumer Protection Law 2023 (fair terms) | `due_at = accepted_at + 72 h`, configurable 1–72 h; late payment accepted and flagged. | Is 72 h fair for consumers? Late-payment interest / penalty (none implemented). |
| 4 | Customer acceptance in the app | Civil Code on contract performance; e-transactions law | Customer ACCEPT / REQUEST_REWORK recorded with time, reason, evidence; technician cannot accept. | Is in-app acceptance sufficient proof of handover, or is a signed minutes required? |
| 5 | Paper signature photographed by the technician | Law on E-transactions 2023 (not an e-signature) | Pages stored with SHA-256, uploader, time, version lock; no e-signature claim. | Retention period; original paper custody; evidentiary value of photos. |
| 6 | Collection practice | Consumer Protection Law 2023 (no harassment), Personal Data Protection decree | Sales records contacts; no automatic messages beyond one in-app overdue notice. | Contact rules (hours, channels, frequency); template wording. |
| 7 | Overdue consequences | BLDS 2015 Art. 357 (interest for late payment), Art. 418 (penalty) | None automatic: no interest, penalty, suspension, repossession or deposit confiscation. | Which consequences are allowed and how they are decided (always by people). |
| 8 | Refunds / unapplied money | BLDS 2015 (unjust enrichment, Art. 579) | Duplicate / wrong / late money goes to the manual refund ledger; no refund button. | Refund deadline and approval rule. |
| 9 | Personal data in contracts and photos | Decree 13/2023/NĐ-CP | Read-all for staff (audited); signed URLs; no public files. | Is read-all for Sales/Technician proportionate? Retention / deletion policy. |
| 10 | Unilateral withdrawal before the deposit | Consumer Protection Law 2023 | Contract discarded, nothing charged. | Cooling-off rules after the deposit. |

Process: Legal reviews the generated text (`PaymentPlanCalculator.Clauses`), returns approved wording, then the clause text and
`PaymentPlan:TermsVersion` are updated in a later release. Contracts already created keep their frozen text and hash.
Collection cases can be marked `LEGAL_REVIEW_REQUIRED` by a Manager/Admin; the case shows it, nothing else happens.
