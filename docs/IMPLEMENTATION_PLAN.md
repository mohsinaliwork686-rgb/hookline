# Implementation plan — hookline

> Written Wednesday 18 November 2026, before any code. The second half of week 9: two and a half days,
> shipping Friday 20 November.

## 1. Problem

When something happens in one system, other systems often need to know: an order was paid, a shift
was swapped, a build finished. The usual way is a webhook: the first system sends an HTTP request to a
URL the other one gave it.

Sending it once is easy. Sending it reliably is not. The receiver can be down, slow, or answer with an
error, and an event that is tried once and dropped is simply lost. So every app that sends webhooks
needs the same machinery: store the event first, retry with growing gaps, sign each request so the
receiver can trust it, set aside what keeps failing so a person can look at it, and never send the same
event twice by accident when several workers share the job. Most apps build this badly, or not at all.

`hookline` is that machinery as a small service. An app posts its events to hookline and gets a quick
answer. hookline stores each event, works out which subscribers want it, and delivers it to each one:
signed, retried with backoff, and set aside in a dead-letter queue after the last attempt. A dashboard
shows every delivery, each attempt and why it failed, with a button to send a dead one again.

`rotadesk` shows I can build what clients ask for. This shows the backend depth behind it: failure,
retries and concurrency, run rather than read about.

## 2. Non-goals

- **Not a message broker.** No topics, consumers or streaming. Events go out over HTTP to subscribers.
- **No ordering guarantee.** A retry can arrive after a newer event. Stripe says the same about its own
  webhooks; receivers that care use the event's timestamp.
- **No exactly-once promise.** Nobody can honestly give one over HTTP. The promise is at least once,
  with a stable id on every request so the receiver can drop repeats.
- **No multi-tenancy, sign-up or user accounts.** One sending app, one API key for it, one admin
  password for the dashboard.
- **No secret rotation with overlapping secrets.** A subscription has one secret; replacing it is a
  new subscription. The signature format leaves room for it (several signatures in one header).
- **No live hosted demo.** The demo is `docker compose up`, with a receiver that fails on purpose.
- **No filtering of internal addresses.** Whoever holds the API key can point a subscription at any
  URL, including ones only hookline's network can reach. The demo receiver is one of those, on the
  compose network. Run it where its network can't reach anything it shouldn't.

## 3. What it does

**Subscriptions.** A subscription is a URL, the event types it wants (or all of them), and a secret.
hookline generates the secret (`whsec_` and 32 random bytes in base64) and shows it once.

```http
POST /api/subscriptions
Authorization: Bearer <api key>
{ "url": "https://receiver.example/hooks", "eventTypes": ["order.paid", "order.refunded"] }

201 Created
{ "id": "sub_...", "url": "...", "eventTypes": [...], "secret": "whsec_..." }
```

**Events.** The sending app posts an event. hookline saves the event and one delivery for each
subscription that wants that type, in one transaction, and only then answers. If the save fails, the
app gets an error and can retry; if it succeeds, nothing is lost from then on.

```http
POST /api/events
Authorization: Bearer <api key>
Idempotency-Key: order-1042-paid
{ "type": "order.paid", "data": { "orderId": 1042, "amount": 3500 } }

202 Accepted
{ "id": "evt_...", "type": "order.paid", "deliveries": 2 }
```

The `Idempotency-Key` is optional. If the app retries the same request, perhaps because its own
connection dropped before the answer came back, the second request returns the first event instead
of creating a duplicate.

**Deliveries.** Each delivery is a `POST` of the event to the subscription's URL:

```http
POST /hooks
Content-Type: application/json
webhook-id: evt_...
webhook-timestamp: 1763470800
webhook-signature: v1,<base64 of the HMAC-SHA256>

{ "id": "evt_...", "type": "order.paid", "timestamp": "2026-11-18T13:00:00Z", "data": { ... } }
```

A `2xx` answer within the timeout means delivered. Anything else is a failed attempt, retried on a
schedule until it succeeds or the attempts run out, and then the delivery is dead.

**The dashboard** (Razor Pages, behind the admin password) lists deliveries by status, shows each
one's attempts with the status code, time taken and the start of the response or the error, and has a
dead-letter view with **Send again**.

## 4. The three decisions the roadmap left for Wednesday

Read first: how Standard Webhooks (an open spec several providers follow), Stripe, GitHub and Svix do
it. The notes, with sources, are in [research-webhooks.md](research-webhooks.md).

### 4a. How many attempts, and the backoff schedule

**Ten attempts over about three days, with jitter: immediately, then 5 seconds, 5 minutes, 30
minutes, 2 hours, 5 hours, 10 hours, 14 hours, 20 hours and 24 hours.** That is the Standard Webhooks
schedule, close to Svix's, and in the range of Stripe's "up to three days". Three days covers a
receiver that is down for a long weekend, without retrying forever.

Each wait gets up to 10% random jitter, so thousands of deliveries that failed at the same moment
don't all retry in the same second. The schedule is configuration, not code: the demo uses a short
one (seconds) so a delivery visibly dies within a minute, and the tests use a fake clock.

The policy is a pure function: given the attempt number and the time, it returns the time of the next
attempt, or "dead". It lives in `Hookline.Core` with no database or HTTP, and is tested on its own.

### 4b. Which responses are worth retrying

**Every failure is retried except `410 Gone`.** A sender can't tell a temporary `400` (a receiver
deploying a bad build) from a permanent one, and the retry window gives the receiver time to fix it.
Stripe, Svix and Standard Webhooks all retry every non-`2xx`. So:

- `2xx` within the timeout: delivered.
- `3xx`: a failure. Redirects aren't followed, because a signed request shouldn't be forwarded
  somewhere the subscriber didn't register.
- `410 Gone`: the receiver says this URL is gone. The delivery is dead at once and the subscription is
  disabled, so no more deliveries are created for it. Its other waiting deliveries end too, without
  being sent, and so does any delivery a worker takes for a subscription that was disabled meanwhile.
- `429` and `503` with a `Retry-After` header: the next attempt waits at least that long (capped at
  the schedule's longest wait), so hookline doesn't hammer a receiver asking it to slow down.
- Everything else, including timeouts, refused connections, DNS failures and other `4xx` and `5xx`:
  a failed attempt, retried on the schedule.

**Timeout: 15 seconds** per attempt, the low end of Standard Webhooks' 15 to 30. A slow receiver
holds up only its own delivery, never the others, because each worker sends a batch in parallel with a
cap.

### 4c. How the signature is laid out

**The Standard Webhooks layout.** Three headers:

- `webhook-id`: the event id. The same on every retry, so the receiver can use it to drop repeats.
- `webhook-timestamp`: Unix seconds when this attempt was sent. New on each attempt.
- `webhook-signature`: `v1,` and the base64 HMAC-SHA256, keyed with the secret's bytes, of
  `{webhook-id}.{webhook-timestamp}.{body}`. Space-separated if there were ever several.

Signing the id and timestamp with the body, rather than the body alone as GitHub does, means a
captured request can't be replayed later: the receiver rejects a timestamp more than 5 minutes off
(Stripe's libraries' default). Following an existing spec also means receivers can verify with a
library they may already have. The demo receiver verifies with hookline's own `Hookline.Core`
verifier, and a test checks the signer against the fixed example in the spec's reference library
tests (its Python tests sign a known id, timestamp and body with a known secret).

## 5. Data model

All in PostgreSQL, through EF Core with snake_case names.

| Table | What it holds |
|-------|---------------|
| `subscriptions` | id, url, event types (`text[]`, empty means all), secret, enabled, created |
| `events` | id, type, data (`jsonb`), idempotency key (unique when present), created |
| `deliveries` | id, event id, subscription id, status, attempts made, next attempt at, locked until, lease id, delivered at, last error, created |
| `delivery_attempts` | id, delivery id, number, started at, duration, status code, error, start of the response body |

A delivery's status is `pending`, `delivered` or `dead`. While a worker holds it, `locked_until` is in
the future; that is the lease (section 6), not a separate status.

Ids are prefixed (`evt_`, `sub_`, `dlv_`) and random, so they're safe to show and hard to guess.

## 6. The delivery worker

A `BackgroundService` runs a loop. Several copies can run at once, in one process or many; the
database decides who sends what.

**Taking work, with `FOR UPDATE SKIP LOCKED`.** In a short transaction, the worker selects up to N
deliveries that are pending, due (`next_attempt_at <= now`) and not leased (`locked_until` empty or
past), with `FOR UPDATE SKIP LOCKED`. Rows another worker is taking at that instant are skipped, not
waited for. It stamps each with a new lease id and `locked_until = now + 2 minutes`, and commits. The
row lock lasts only for that transaction; the lease is what keeps other workers away while it sends.

**Sending.** Outside any transaction, so no database connection is held during a slow HTTP call, the
worker signs and sends each delivery, in parallel up to a limit, with the 15-second timeout.

**Recording.** In another short transaction, the worker writes the attempt and moves the delivery on:
delivered, scheduled for the next attempt, or dead. It updates the delivery only if the lease id is
still its own, so a worker that was too slow and lost its lease can't overwrite a newer result. That
worker writes nothing at all, not even its attempt, so the log can miss a request the receiver did get.

**Nothing is lost.** If a worker dies after taking a delivery, the lease simply runs out and another
worker takes it. The receiver may then see the event twice, which is exactly what at least once means
and why `webhook-id` is stable.

**No double work.** Two workers can never hold the same delivery at the same time: `SKIP LOCKED`
stops them taking it together, and the lease stops one taking it while the other sends.

**Send again.** From the dashboard, a dead delivery goes back to pending with its attempt count kept
and its next attempt now. A sent-again delivery that fails is dead again after one more attempt.

## 7. Projects

| Project | Job |
|---------|-----|
| `src/Hookline.Core` | **Pure C#.** The retry policy, signing and verifying, ids. No database, no HTTP |
| `src/Hookline.Api` | The ingest API, the delivery worker, the dashboard (Razor Pages), EF Core and migrations |
| `src/Hookline.DemoReceiver` | A small app that succeeds, fails, hangs or answers `410` on purpose, and verifies signatures |
| `tests/Hookline.Core.Tests` | The policy and signing, fast, with a fake clock |
| `tests/Hookline.Api.Tests` | The API and the worker against real PostgreSQL in Testcontainers, and a fake receiver |

The same stack as `rotadesk`: .NET 10, ASP.NET Core minimal APIs, EF Core with Npgsql, xUnit v3 on
Microsoft.Testing.Platform, Testcontainers, `TimeProvider` for the clock.

## 8. The demo

`docker compose up` starts PostgreSQL, hookline and the demo receiver. The receiver also plays the
sending app: it creates a subscription for each path and posts a stream of events. (Changed on
Friday from a separate one-off container: the receiver needs each subscription's secret to check
signatures, and creating the subscriptions itself is the simplest way for it to have them.)

| Receiver path | Behaviour | What it shows |
|---------------|-----------|---------------|
| `/ok` | answers `200` | delivered on the first attempt |
| `/flaky` | fails twice per event, then `200` | retries with backoff, then delivered |
| `/slow` | waits 20 seconds | the timeout, without holding up the others |
| `/down` | always `500` | every attempt, then the dead-letter queue and Send again |
| `/gone` | answers `410` | dead at once, subscription disabled |

The demo's schedule is a few seconds per step, so the whole story plays out in about a minute on the
dashboard. The receiver logs each request it gets and whether the signature checked out.

## 9. Test plan

| Area | Must work | Must not happen |
|------|-----------|-----------------|
| Ingest | event and its deliveries saved in one transaction before `202`; deliveries only for subscriptions that want the type | an event saved with some deliveries missing; a delivery for a disabled subscription |
| Idempotency | the same key returns the first event | a duplicate event or duplicate deliveries |
| Nothing is lost | a worker that dies after taking a delivery: the delivery is taken again once the lease runs out | a delivery stuck forever |
| No double work | several workers on one table: each delivery attempted once per due time | two workers sending the same delivery at once; a worker that lost its lease overwriting the result |
| Retries | a receiver that fails twice then succeeds is delivered on attempt three; backoff times checked with a fake clock, jitter within 10% | retrying a delivered delivery; waits outside the schedule |
| Dead letters | after the last attempt the delivery is dead; Send again delivers it | retrying forever; Send again on a delivered delivery |
| Responses | `3xx` is a failure; `410` kills the delivery and disables the subscription; `Retry-After` is honoured and capped | following a redirect; a `410` retried |
| Signing | the reference library's fixed example signs and verifies; a changed body, wrong secret or old timestamp fails | a signature that ignores the id or timestamp |
| Slow receiver | times out at 15 seconds without holding up others in the batch | one slow receiver delaying the rest |
| Database | migrations apply to an empty PostgreSQL; the claim query runs on real PostgreSQL | tests that only pass against a fake |

## 10. Build order

- **Wednesday afternoon:** this plan; the solution; `Hookline.Core` with the retry policy and signing,
  and their tests; the database, migrations and the ingest API with idempotency, tested against
  PostgreSQL.
- **Thursday:** the delivery worker: claiming with `SKIP LOCKED` and leases, sending, recording,
  `410` and `Retry-After`; the worker tests (lost lease, several workers, retries with a fake clock,
  slow receiver).
- **Friday:** the dashboard and Send again; the demo receiver and compose; CI; the README with a
  screenshot; publish; the weekly updates.

## 11. Risks

| Risk | What I'll do |
|------|--------------|
| Two and a half days is tight | The worker and its tests come before the dashboard. The dashboard stays plain: tables, a detail page, one button |
| Concurrency tests can be flaky | Workers in tests are driven step by step where possible, and the several-workers test checks invariants (each delivery once per due time), not timing |
| A real clock makes retry tests slow | `TimeProvider` everywhere; the schedule is configuration |
| Docker on this machine also starts an unrelated cluster | Tests use only their own Testcontainers PostgreSQL; CI runs them on GitHub's Linux runners |

## 12. Definition of done

- [x] Public repo, description and topics
- [x] This plan, committed before the code
- [x] README: the problem first, a screenshot of the dashboard, how to run the demo, the guarantees and what it doesn't promise
- [x] Every row of the test plan covered
- [x] CI green: format, build with warnings as errors, the core tests, the API and worker tests against real PostgreSQL
- [x] The compose demo: delivered, retried, timed out, dead and sent again, and gone
- [x] At least 10 commits, one logical change each

## 13. How it went

- **Wednesday 18 November:** this plan and the research notes, then `Hookline.Core` (retry policy,
  signing, checked against the reference library's fixed example), the data model and the ingest API
  with idempotency, and CI on three operating systems.
- **Thursday 19 November:** the delivery worker and its tests. The tests were checked by breaking the
  worker on purpose; the first round showed that removing `SKIP LOCKED` went unnoticed, because the
  several-workers test only checks the outcome, so a test now holds a row lock mid-claim and requires
  the worker to skip it. Also found: a receiver answering with a NUL byte made the attempt impossible
  to save in PostgreSQL, so the excerpt replaces it.
- **Friday 20 November:** writing the demo showed that the background loop ran whole passes, so a
  receiver that hung until the timeout held up every delivery that fell due meanwhile. It now keeps
  requests in flight and takes new work as each one finishes, with a test that fails on the old loop.
  Then the dashboard and Send again, the demo receiver and compose (where Npgsql's Kerberos probe
  failed on the aspnet image, as it did in `rotadesk`), the screenshots and the README.

Left for later: keeping the dashboard's sign-in keys in the database (a restart signs the admin out),
a page of subscriptions with a way to enable one again, and paging past the newest 100 deliveries.
