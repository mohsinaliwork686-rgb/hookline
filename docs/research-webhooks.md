# How real services sign and retry webhooks (read 18 Nov 2026, for the plan)

Sources: Standard Webhooks spec (github.com/standard-webhooks/standard-webhooks, spec/standard-webhooks.md),
Stripe (docs.stripe.com/webhooks), GitHub (docs.github.com/en/webhooks/...), Svix (docs.svix.com/retries).

| | Standard Webhooks | Stripe | GitHub | Svix |
|---|---|---|---|---|
| Signature header | `webhook-signature: v1,<base64>` (space-separated list for rotation) | `Stripe-Signature: t=<ts>,v1=<hex>` (one v1 per active secret, up to 7 days) | `X-Hub-Signature-256: sha256=<hex>` | (implements Standard Webhooks) |
| Signed content | `{msg_id}.{timestamp}.{body}` | `{timestamp}.{body}` | body only | |
| Algorithm | HMAC-SHA256, base64; secret `whsec_` + base64 (24–64 bytes) | HMAC-SHA256, hex; secret `whsec_...` | HMAC-SHA256, hex | |
| Replay protection | `webhook-timestamp` (Unix s), "allowable tolerance" (no number) | libraries default to 5 minutes | none | |
| Id for dedupe | `webhook-id`, same across retries | event `id`; retries get new timestamp + signature | (X-GitHub-Delivery, not in pages read) | |
| Retries | 10: now, 5s, 5m, 30m, 2h, 5h, 10h, 14h, 20h, 24h (+ jitter) | up to 3 days, exponential, intervals unpublished | none automatic; manual within 3 days | 8: now, 5s, 5m, 30m, 2h, 5h, 10h, 10h (~27.6h) |
| Success | 2xx only; 3xx is failure | 2xx; 3xx failure | 2xx | 2xx within 30s; 3xx failure |
| Special codes | 410 = disable endpoint; 429/502/504 = throttle; honour `retry-after` | | | `webhook-delivery: abort-message` header stops retries |
| Timeout | 15–30 s | unspecified | 10 s | 30 s |
| After last attempt | notify, disable future delivery, manual replay | disabled endpoint stops retries; resend 15/30 days | | `Failed`, `message.attempt.exhausted` event; endpoint disabled after 5 days of failures |
| Ordering | not covered | explicitly none | not covered | |

Agree: 2xx success, 3xx failure, exponential backoff over hours to days, manual replay, dedupe by a stable id.
Differ: what is signed (body / ts.body / id.ts.body), replay window, retry window (0 to ~3 days), timeout (10–30 s), rotation.
