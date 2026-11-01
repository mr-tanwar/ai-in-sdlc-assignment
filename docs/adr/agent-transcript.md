# 1. Initial comparison

Exactly-once PayEx submission: options and recommendation

Context

Approved invoices must reach PayEx exactly once. Go-live is January, about 40,000 invoices/month, 60% in the last 3 working days (about 8,000/day), peak about 80 approvals/min (about 1.3/s). PayEx times out at 30 s, and a timeout does not reliably mean failure. Team of 6 (2 new), Azure App Service + Azure SQL, Service Bus provisioned but unused, SOC 2 in 6 months.

Two facts from the code (as-built) shape every option:

- Nothing in backend/ calls PayEx today. ApprovalService.Approve does read (Find) → EnsurePending → mutate → Update with no lock or version check, so two concurrent approvals of the same invoice can both succeed. Any option needs a concurrency guard in SQL, or one invoice can trigger two submissions.
- INC-2041 (ops/, planned system, used here as scenario evidence only): PayEx called inside the approval request caused socket exhaustion and 100 s timeouts and made approvals return 5xx. A submission option that keeps PayEx in the request path repeats that incident.

Core point: exactly-once needs PayEx's help

Nothing on our side can make a call to an external system exactly-once. What we can build is at-least-once delivery plus a receiver that ignores repeats (idempotency), or a way to ask PayEx "did you get this one?". So the real question is which option gives reliable delivery and what it needs from PayEx. Options 1 and 2 solve different halves of the problem, and 3 gives up the guarantee.

Assumptions about PayEx (to be confirmed with PayEx before building)

┌─────┬───────────────────────────────────────────────────────────────────────────────────────────────────────────────┬────────────────────────────────────────────────────────────────────────────────────┐
│ # │ Assumption │ If false │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A1 │ PayEx accepts a client-supplied idempotency key and returns the original result (including the reference) for │ Fall back to A2. │
│ │ a repeat of the same key. │ │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A2 │ Alternatively, PayEx can look up a submission by our own reference (the invoice id), so we can ask "was INV-x │ Exactly-once cannot be guaranteed. Only at-most-once with manual resolution of │
│ │ received?" │ unknowns is possible. │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A3 │ Key retention window is at least our longest retry horizon (assume at least 7 days). │ Retries after expiry could duplicate; cap retries inside the window. │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A4 │ Two simultaneous requests with the same key are serialised or the second is rejected (409). They are not both │ In-flight races duplicate; claiming rows must guarantee one caller per invoice. │
│ │ processed. │ │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A5 │ A timeout (30 s) and any 5xx are ambiguous. A 4xx validation rejection is definitive and has no side effect. │ If 4xx can also have side effects, treat those as unknown too. │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A6 │ PayEx rate limit is at least 80 requests/min with margin. Unknown, so worker concurrency must be │ Throttle the worker; backlog grows at month end. │
│ │ configurable. │ │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A7 │ PayEx provides a settlement report or export we can match against by reference. Needed for reconciliation in │ Reconciliation becomes manual. │
│ │ any option. │ │
├─────┼───────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
│ A8 │ The reference returned on success is stable and unique per submission. │ Cannot use it as proof of success. │
└─────┴───────────────────────────────────────────────────────────────────────────────────────────────────────────────┴────────────────────────────────────────────────────────────────────────────────────┘

Everything else about PayEx is unknown. A1 or A2 is the one blocking question for the vendor.

Comparison

What it is

1.  Idempotency key + dedupe table: Before calling PayEx, record (invoiceId/key → state, reference) in a dedupe table. Send the key to PayEx. Retries reuse the key.
2.  Transactional outbox + background worker: Approval writes Approved and an Outbox row in one SQL transaction. A worker claims rows, calls PayEx with a stable key, records the reference.
3.  Optimistic submission + nightly reconciliation: Call PayEx once right after approval and carry on. A nightly job compares our records with PayEx's report and repairs differences.
    ────────────────────────────────────────
    PayEx timeout
4.  Idempotency key + dedupe table: Row stays InProgress/Unknown. A retry with the same key is safe only if PayEx honours it (A1). Without A1, a lookup (A2) is needed before retrying.
5.  Transactional outbox + background worker: Row goes to RetryScheduled with backoff and is retried with the same key. With A2, the worker looks up first. After N attempts it moves to Unknown and alerts a
    human.
6.  Optimistic submission + nightly reconciliation: Treated as success or failure by guess. The truth shows up the next morning, up to about 24 h later.
    ────────────────────────────────────────
    Failure modes
7.  Idempotency key + dedupe table: (a) The table only covers our own retries. A crash after PayEx accepts but before we write the reference leaves Unknown, and without A1 the retry duplicates. (b) It has no
    retrier: if the call fails after the approval commits, the invoice is Approved but never submitted unless something sweeps. (c) If the call happens inside the request, the INC-2041 failure repeats. (d) If
    the call happens before the approval commits, approval depends on PayEx being up.
8.  Transactional outbox + background worker: (a) Duplicates if A1 and A2 are both false (same limit as option 1, but the outbox row makes the unknown state visible and auditable). (b) Worker crash mid-call:
    the lease expires and the row is retried with the same key. (c) Double approval creates two rows: prevented by a unique index on Outbox.InvoiceId and a row version on the invoice. (d) Backlog at month end
    if PayEx is slow (see capacity).
9.  Optimistic submission + nightly reconciliation: (a) Duplicate payments are only discovered after the fact and must be reversed. This breaks "exactly once" as a guarantee. (b) Missed submissions wait until
    the next night, and at month end that is days of delay for most of the volume. (c) Inline call repeats INC-2041. (d) Reconciliation itself needs A7 and a repair procedure.
    ────────────────────────────────────────
    Operational complexity
10. Idempotency key + dedupe table: Low code, but hidden gaps: needs a sweeper (which turns it into option 2), plus alerting on stuck InProgress rows.
11. Transactional outbox + background worker: Moderate: one table, one BackgroundService, a row state machine (Pending, InFlight, Succeeded, RetryScheduled, Unknown, Failed), a lease, backoff, an alert on
    Unknown and on backlog age, plus a dead-letter/manual queue.
12. Optimistic submission + nightly reconciliation: Lowest to build, highest to operate: a manual repair process every morning, and money-movement errors to unwind.
    ────────────────────────────────────────
    Fit for this team (6, 2 new)
13. Idempotency key + dedupe table: Easy to build, but the missing retry story gets bolted on later under pressure.
14. Transactional outbox + background worker: Well-known pattern, mostly plain SQL and one worker class. It fits the repo rules (business rule in Domain, IInvoiceRepository port, fakes in tests). Testable
    without PayEx by faking the client.
15. Optimistic submission + nightly reconciliation: Easiest first sprint, hardest to defend to a SOC 2 auditor.
    ────────────────────────────────────────
    SOC 2 (processing integrity)
16. Idempotency key + dedupe table: Weak: no durable record of attempts.
17. Transactional outbox + background worker: Strong: every attempt (time, outcome, reference) is a row, and the approval and the intent to submit commit together.
18. Optimistic submission + nightly reconciliation: Weak as a preventive control. Good as a detective control.
    ────────────────────────────────────────
    Needs Service Bus?
19. Idempotency key + dedupe table: No
20. Transactional outbox + background worker: No. SQL is enough for submission. The unused Service Bus can later carry events from the same outbox.
21. Optimistic submission + nightly reconciliation: No
    ────────────────────────────────────────
    Meets "exactly once"?
22. Idempotency key + dedupe table: Only with A1 or A2, and still lacks reliable delivery.
23. Transactional outbox + background worker: Yes with A1 or A2. Without them, it gets at-most-once with explicit Unknown handling and no silent duplicates.
24. Optimistic submission + nightly reconciliation: No. Detect-and-repair after the money has moved.

Capacity check (option 2)

- Peak 80/min is about 1.3/s. In-flight calls needed = rate × latency. If PayEx averages 2 s, about 3 concurrent. If every call hits the 30 s timeout, about 40.
- So worker concurrency must be configurable (start around 16, raise with monitoring). The lease on a claimed row must be longer than the client timeout (client timeout about 35 s, lease about 2 min), or a second worker picks up a call still in flight.
- Use IHttpClientFactory/a typed client, not a singleton client that creates its own handlers (the INC-2041 diff did that).

Recommendation: Option 2, with the idempotency key from option 1 and a small reconciliation report from option 3

1.  Outbox with worker is the only option that makes delivery reliable (approval and intent to submit commit together) and keeps PayEx out of the approval request.
2.  Send a stable idempotency key with every attempt: the invoice id, or a GUID created with the outbox row. This gives the exactly-once effect, provided A1 or A2 holds. The dedupe table in option 1 is justthis outbox row's Succeeded + reference.
3.  Keep a daily reconciliation report (outbox vs PayEx report, A7) as a detective control for SOC 2. It is a safety net, not the guarantee.
4.  Timeout rule: a timeout or 5xx never marks the row failed. It schedules a retry with the same key (or a lookup first under A2). A 4xx marks Failed and alerts. After N attempts or a lease-expiry storm, the row becomes Unknown, pages a human, and does not retry automatically.

Why not the others: option 1 alone has no retry path and leaves a crash window. Option 3 gives up the stated requirement.

Decisions I need from you / PayEx before building

- Does PayEx support A1 (idempotency key) or A2 (lookup by our reference)? This decides whether the guarantee is real. If neither: do you accept at-most-once with manual handling of Unknown?
- Worker hosting: separate WebJob/App Service vs hosted service inside the API (the latter means every scaled-out instance polls, so the claim query must be safe: UPDLOCK, READPAST or UPDATE … OUTPUT). App Service "Always On" must be enabled either way.
- Retention window and rate limit from PayEx (A3, A6).

Implementation touchpoints (for a later build, not part of this deliverable)

- InvoiceApp.Domain: IInvoiceRepository has only Update; approval and outbox insert need one atomic operation (new port method or unit of work). ApprovalService.Approve stays the single place the rule lives.
- Row version on Invoice + unique index on Outbox(InvoiceId) to stop double approval enqueueing two submissions.
- A PayExClient interface in Domain with a fake for tests, per the "use fakes" convention.
- Tests: timeout then retry with same key; crash after PayEx success; double approval; 4xx; lease expiry.
