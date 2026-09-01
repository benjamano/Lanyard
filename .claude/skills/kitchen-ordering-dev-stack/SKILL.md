---
name: kitchen-ordering-dev-stack
description: Run the QR food ordering stack locally end to end - Lanyard server, the public Lanyard.Reach site, and Stripe test payments including webhooks. Use when testing customer ordering, the kitchen display, payment confirmation, or refunds, or when payments "work" but orders never reach the kitchen.
---

# Running QR ordering locally

Ordering spans three processes that must all be up: the Lanyard server (API, kitchen screens),
Lanyard.Reach.Web (the customer site), and the Stripe CLI forwarding webhooks. Miss the third
and payments succeed but orders sit at `AwaitingPayment` until the status poll rescues them.

## 1. Database

```bash
docker compose up -d postgres
dotnet ef database update --project src/Lanyard.Infrastructure --startup-project src/Lanyard.Server/LanyardApp
```

If compose reports the container name is already in use, the container exists from an earlier
run under a different compose project — `docker start lanyard-postgres` rather than recreating
it, which would discard the local data.

If `dotnet ef` fails with ".NET location: Not found", set `DOTNET_ROOT` (on this machine
`DOTNET_ROOT=/usr/local/dotnet`). The tool resolves the runtime independently of `dotnet` itself.

## 2. Stripe credentials

`src/Lanyard.Server/LanyardApp/appsettings.local.json` — loaded by `Program.cs` and gitignored.
Only the server needs these; Reach receives the publishable key through the API.

```json
{
  "Stripe": {
    "PublishableKey": "pk_test_...",
    "SecretKey": "sk_test_...",
    "WebhookSecret": "whsec_..."
  }
}
```

Without them, ordering refuses with "Card payments are not set up" rather than failing obscurely.

## 3. Stripe webhook forwarding

```bash
stripe listen --forward-connect-to http://localhost:5096/api/ordering/payments/webhook
```

**`--forward-connect-to`, not `--forward-to`.** Charges are *direct charges on the venue's
connected account*, so `payment_intent.succeeded` fires on that account, not the platform.
Plain `--forward-to` forwards only platform events, so payments succeed and the kitchen never
sees the order. This is the single easiest thing to get wrong.

`stripe listen` prints the `whsec_` for step 2. It is stable across restarts for the same
account, so that is a one-time copy rather than something to redo every session.

`--api-key sk_test_...` works instead of `stripe login`, which is useful in a headless
environment where the browser sign-in flow is not available.

## 4. The two apps

```bash
# terminal 1
cd src/Lanyard.Server/LanyardApp   && dotnet run --launch-profile http   # http://localhost:5096

# terminal 2
cd src/Lanyard.Reach/Lanyard.Reach.Web && dotnet run --launch-profile http   # http://localhost:5107
```

`localhost` is seeded as Play2Day's `CompanyDomain`, so Reach on port 5107 resolves to that
tenant. An unmapped host resolves to nothing and the site 404s by design.

## 5. Venue setup (once)

- **Companies & Locations** → set the company's **Stripe account ID** (`acct_...`). Until it is
  set, ordering is refused rather than charging some other account.
- **Kitchen → Menu & Table Codes** → pick the venue, switch **Accept QR orders** on, add a menu
  section, a dish, and a table.

## 6. Ordering

Get a table token and open the customer page:

```bash
docker exec lanyard-postgres psql -U lanyard_dev -d lanyarddb -t -A -F'|' \
  -c 'SELECT "Label","Token" FROM "QrTableTokens" WHERE "IsActive";'
# then: http://localhost:5107/order/t/<token>
```

**Do not scan the printed QR code locally.** It encodes `https://{primary host}/order/t/{token}`
— on a dev box that is `https://localhost`, with no port and no TLS, so it will not reach
port 5107. The QR is correct for production and useless locally; use the URL directly.

Test cards (any future expiry, any CVC, any postcode):

| Card | Expected |
|---|---|
| `4242 4242 4242 4242` | Succeeds; ticket appears on **Kitchen → Orders** once the webhook lands |
| `4000 0000 0000 0002` | Declined; basket kept, no kitchen ticket, nothing charged |

## Checking it worked

```bash
# order state: Status 1=Received 5=Cancelled 6=AwaitingPayment | Payment 3=Pending 4=Paid 5=Refunded
docker exec lanyard-postgres psql -U lanyard_dev -d lanyarddb -t -A -F'|' \
  -c 'SELECT "Id","Status","PaymentStatus","TotalCents" FROM "KitchenOrders" ORDER BY "Id" DESC LIMIT 5;'

# what the kitchen sees, without opening a browser
curl -s "http://localhost:5096/api/kitchen/1/queue" -H "X-Lanyard-Client-Secret: dev"
```

The `stripe listen` window should show `200` for every event, including `charge.succeeded` and
`charge.updated` — those are not ours, but a non-200 makes Stripe retry for days and the
endpoint look broken.

## When it goes wrong

| Symptom | Cause |
|---|---|
| "This venue isn't set up to take payments yet" | Company has no `StripeAccountId` |
| "Card payments are not set up" | `Stripe:SecretKey` / `PublishableKey` missing from `appsettings.local.json` |
| Paid, but no kitchen ticket, tracker stuck on "Confirming your payment" | Webhook not arriving — usually `--forward-to` instead of `--forward-connect-to`. The status poll reconciles directly with Stripe within a few seconds, so this self-heals and can hide the misconfiguration |
| Webhook returns 400 | Wrong `whsec_` — re-copy it from the `stripe listen` output |
| Stripe refuses to create a connected account via `/v1/accounts` | Stripe has deprecated Accounts v1 for new Connect integrations; use `POST /v2/core/accounts` |
| Connected account shows `charges_enabled: false` | Onboarding incomplete. In test mode fill identity, attach a test bank account (GB sort code `108800`, account `00012345`) and use DOB `1901-01-01`, which forces verification to pass |
