# hookline

Delivers webhooks reliably, even when the receiver is down. An app posts its events to hookline,
which stores each one first, then delivers it to every subscriber: signed, retried with backoff, and
set aside for a person to look at after the last attempt. A dashboard shows every delivery and why it
failed, with a button to send a dead one again.

ASP.NET Core, EF Core and PostgreSQL, with `FOR UPDATE SKIP LOCKED` so several workers share the job
without sending anything twice by accident.

**Being built from 18 to 20 November 2026.** The plan is in
[docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md), written before any code.
