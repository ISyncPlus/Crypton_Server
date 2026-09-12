# Crypton — API (Crypton_Server)

The backend for Crypton, a naira ⇄ crypto exchange: BTC, ETH and USDT with instant buy/sell/swap,
wallets and deposits/withdrawals, a double-entry ledger, KYC tiers and limits, AML rules, P2P trading
with escrow, analytics and reports, and a staff back office.

ASP.NET Core 10 · PostgreSQL · EF Core · JWT with refresh cookies · background jobs in-process.
The web client lives in the companion repository (`Crypton.git`) and talks to this API under `/api`.

- [What is in here](#what-is-in-here)
- [Run it locally](#run-it-locally)
- [Run it with Docker](#run-it-with-docker)
- [Configuration you must fill in](#configuration-you-must-fill-in)
- [Going live: step by step](#going-live-step-by-step)
- [Tests](#tests)
- [Day-to-day operations](#day-to-day-operations)
- [Security notes and known limits](#security-notes-and-known-limits)

## What is in here

```
src/Crypton.Api            HTTP layer: controllers, auth, contracts, seeding, background job runner
src/Crypton.Core           Domain: ledger, trading, wallets, fiat, KYC, AML, P2P, admin, notifications
src/Crypton.Integrations   Outside world: Bitcoin, Ethereum, Paystack, CoinGecko, Dojah, SMTP, sanctions
tools/Crypton.KeyTool      Generates wallet keys, xpubs and secrets (see below)
tests/Crypton.Tests        Unit and integration tests (integration tests need PostgreSQL)
```

Every balance change goes through `LedgerService` as a balanced double-entry journal. Money is `decimal`
end to end and is serialised as a string in JSON so no precision is lost in the browser.

## Run it locally

Requirements: .NET SDK 10, PostgreSQL 15+, and (optionally) [Mailpit](https://mailpit.axllent.org) to read
the emails the API sends.

```bash
# 1. database
createuser crypton --createdb --pwprompt      # password: crypton (matches appsettings.Development.json)
createdb -O crypton crypton_dev

# 2. mail catcher (optional but recommended — confirmation and reset links arrive here)
mailpit                                       # UI on http://localhost:8025

# 3. API
dotnet restore Crypton.slnx
dotnet run --project src/Crypton.Api          # http://localhost:5080
```

`appsettings.Development.json` is wired for a self-contained dev stack: simulated blockchain, simulated
payments, simulated prices, demo data, and the API reference at <http://localhost:5080/scalar>. Migrations
are applied on start-up.

Seeded sign-ins in development:

| Account | Email | Password |
| --- | --- | --- |
| Admin (all staff roles) | `admin@crypton.local` | `Admin-Password-2026` |
| Demo customer | `ada@demo.crypton.local` | `Demo-Password-2026` |
| Demo customer | `tunde@demo.crypton.local` | `Demo-Password-2026` |

Change or remove these before any deployment — they only exist because `Seed:DemoData` is `true` in
Development.

Local overrides that should never be committed go in `src/Crypton.Api/appsettings.Local.json` (git-ignored)
or in user secrets (`dotnet user-secrets --project src/Crypton.Api set "Payments:Paystack:SecretKey" "sk_test_…"`).

## Run it with Docker

Brings up PostgreSQL, Mailpit, the API and the web app together. Requires a checkout of the frontend
repository next to this one.

```bash
cp .env.example .env      # fill in the REQUIRED values
docker compose up --build
```

- Web: <http://localhost:4200>
- API: <http://localhost:5080>
- Mail: <http://localhost:8025>

`WEB_PATH` in `.env` points at the frontend checkout (default `../P2P Frontend`). The web container serves
the built Angular app and forwards `/api` to the API container, so the browser only ever talks to one origin.

> Docker was not available on the machine this was built on, so the compose stack and the two Dockerfiles
> have not been run end to end. They are written to the same configuration the local stack uses; if a build
> step needs adjusting, that is where to look first.

## Configuration you must fill in

Every setting can be supplied as an environment variable using `__` for nesting
(`Blockchain__Bitcoin__AccountXpub`). Empty values in `appsettings.json` are the placeholders you replace.

| Setting | What it is | Where to get it |
| --- | --- | --- |
| `ConnectionStrings:Default` | PostgreSQL connection | Your database host |
| `Jwt:SigningKey` | Signs access tokens (base64, 32+ bytes) | `dotnet run --project tools/Crypton.KeyTool -- secrets` |
| `Kyc:IdHashKey` | Key that hashes NIN/BVN numbers at rest | Same command. **Changing it breaks duplicate detection for existing records** |
| `App:FrontendBaseUrl` | Public URL of the web app | Your domain. Used for links in emails and payment redirects |
| `App:ApiBaseUrl` | Public URL the browser calls | Usually the same domain |
| `Seed:AdminEmail` / `Seed:AdminPassword` | First staff account, created once | Choose them; sign in, turn on 2FA, then change the password |
| `Pricing:CoinGecko:ApiKey` | Live prices | <https://www.coingecko.com/en/api> — Demo plan is free; set `Pricing:Provider=CoinGecko` |
| `Blockchain:Bitcoin:AccountXpub` | Watch-only key that derives customer BTC deposit addresses | Key tool, step 2 below |
| `Blockchain:Bitcoin:HotWalletWif` | Key that signs BTC withdrawals | Key tool, step 2 below |
| `Blockchain:Bitcoin:ApiBaseUrl` | mempool.space-compatible API | `https://mempool.space/api` (mainnet) or `https://mempool.space/testnet4/api` |
| `Blockchain:Bitcoin:ExplorerUrl` | Overrides the explorer used for links | Optional; defaults follow the network |
| `Blockchain:Ethereum:RpcUrl` | JSON-RPC endpoint | Alchemy, Infura, QuickNode or your own node |
| `Blockchain:Ethereum:ChainId` | 1 mainnet, 11155111 Sepolia | Matches the RPC endpoint |
| `Blockchain:Ethereum:AccountXpub` | Derives customer ETH/USDT deposit addresses | Key tool |
| `Blockchain:Ethereum:HotWalletPrivateKey` | Signs ETH and USDT withdrawals | Key tool |
| `Blockchain:Ethereum:UsdtContract` | USDT (ERC-20) contract address | `0xdAC17F958D2ee523a2206206994597C13D831ec7` on mainnet; on a testnet use your own test token |
| `Payments:Paystack:SecretKey` | Naira deposits and payouts | Paystack dashboard → Settings → API Keys & Webhooks |
| `Kyc:Dojah:AppId` / `Kyc:Dojah:SecretKey` | Automatic NIN/BVN checks | <https://dojah.io> dashboard; set `Kyc:Provider=Dojah` (leave `Manual` to review by hand) |
| `Compliance:SanctionsOracle:RpcUrl` | Reads Chainalysis' on-chain sanctions oracle | Any Ethereum **mainnet** RPC URL; set `Enabled=true` |
| `Email:Smtp:*` | Outgoing email | Your SMTP provider (SendGrid, Postmark, Amazon SES…) |
| `DataProtection:KeysPath` | Where refresh-token and 2FA protection keys are written | A **persistent** volume. Losing it signs everyone out |
| `Storage:LocalPath` | Where KYC documents are written | A persistent, private volume |

### The key tool

```bash
dotnet run --project tools/Crypton.KeyTool -- secrets                     # JWT + ID hash keys
dotnet run --project tools/Crypton.KeyTool -- generate --network testnet  # wallet seed, xpubs, hot keys
dotnet run --project tools/Crypton.KeyTool -- generate --network mainnet
dotnet run --project tools/Crypton.KeyTool -- derive --btc-xpub <xpub> --count 5
```

`generate` prints three blocks:

1. **A 24-word seed.** Write it on paper, store it offline, and never put it on a server. It controls every
   customer deposit address, so it is how you sweep deposits later (BIP84 for Bitcoin, `m/44'/60'/0'/0/i`
   for Ethereum).
2. **Account extended public keys.** Safe on the server — they only derive addresses, they cannot spend.
3. **Hot wallet keys plus their addresses.** Put the keys in your secret manager and fund the addresses with
   only as much as you need for a day of withdrawals.

## Going live: step by step

1. **Generate secrets**: `… KeyTool -- secrets` → `Jwt:SigningKey`, `Kyc:IdHashKey`.
2. **Generate wallets**: `… KeyTool -- generate --network mainnet` (start with `testnet` for a rehearsal).
   Store the seed offline, put the xpubs in config, put the hot keys in your secret manager, fund the hot
   wallet addresses.
3. **Set `Blockchain:Mode=Live`** and fill in the Bitcoin and Ethereum sections, including
   `Ethereum:UsdtContract` and a `Ethereum:RpcUrl` that matches `ChainId`.
4. **Prices**: `Pricing:Provider=CoinGecko` with an API key.
5. **Payments**: `Payments:Provider=Paystack` with the secret key, then add the webhook.
   In Paystack → Settings → API Keys & Webhooks, set the webhook URL to `https://<your-api>/api/webhooks/paystack`.
   The API verifies the `x-paystack-signature` header, so nothing else is needed. Payouts to bank accounts
   use the Transfers API — enable transfers on the account and complete Paystack's OTP/approval settings
   (with OTP on, each payout waits for your confirmation in the Paystack dashboard).
6. **Identity checks**: keep `Kyc:Provider=Manual` to review submissions in the back office, or set `Dojah`
   with credentials for automatic NIN/BVN verification.
7. **Sanctions screening**: set `Compliance:SanctionsOracle:Enabled=true` with a mainnet RPC URL.
8. **Email**: real SMTP settings and a sending domain you control (SPF/DKIM), otherwise confirmation emails
   land in spam.
9. **Hosting**: run behind HTTPS. Set `App:TrustForwardedHeaders=true` when a reverse proxy terminates TLS,
   keep `Jwt:RefreshCookieSecure=true`, serve the web app and the API on the same site so the refresh cookie
   works, and mount persistent volumes for `DataProtection:KeysPath` and `Storage:LocalPath`.
10. **First sign-in**: set `Seed:AdminEmail`/`Seed:AdminPassword`, start the API once, sign in, turn on
    two-factor authentication, change the password, then clear those two settings.
11. **Check the back office → Health page**: it shows job status, the price feed, and a ledger balance check.

`ASPNETCORE_ENVIRONMENT=Production` refuses to start with a simulated blockchain or simulated payments
(unless `App:AllowSimulationInProduction=true`, which is only meant for a public demo) and refuses to start
with `Dev:EnableEndpoints=true`.

## Tests

```bash
dotnet test Crypton.slnx
```

102 tests. The integration tests spin the API up in memory and create a temporary database per test class,
so PostgreSQL must be reachable. By default they connect to
`Host=localhost;Port=5432;Username=crypton;Password=crypton;Database=postgres`; override with the
`CRYPTON_TEST_POSTGRES` environment variable. The account needs permission to create and drop databases.

The GitHub Actions workflow in `.github/workflows/ci.yml` runs restore, build, the full test suite against a
PostgreSQL service container, a check that no EF model changes are missing a migration, and an API image build.

### End-to-end tests

The Playwright suite lives in the frontend repository and drives a real browser against a running stack:
this API on `:5080` with a fresh development database, the web app on `:4200`, and Mailpit on `:8025`
(it reads confirmation links out of the mailbox). See that repository's README.

## Day-to-day operations

- **Migrations**: `dotnet ef migrations add <Name> --project src/Crypton.Core --startup-project src/Crypton.Api --output-dir Data/Migrations`.
  They are applied on start-up; set `Database:MigrateOnStartup=false` and run
  `dotnet ef database update` yourself if you prefer.
- **Background jobs** run inside the API process (deposit scanning, withdrawal broadcasting, payouts,
  reconciliation, P2P expiry, price refresh, email outbox, cleanup). The back office → Health page lists the
  last success and failure of each. Run a single API instance, or set `Jobs:Enabled=false` on the extra
  instances so only one runs them.
- **Reports**: back office → Reports exports CSVs (trades, ledger, P2P, users, deposits, withdrawals) for a
  date range, in the `Reporting:TimeZone` time zone (Africa/Lagos by default).
- **Rate limits** are on by default (`RateLimiting`). Loosen or tighten per environment.

## Security notes and known limits

- Passwords use ASP.NET Identity hashing; sessions are refresh-token based with reuse detection, and
  two-factor authentication (TOTP + recovery codes) is required for staff by default
  (`Security:RequireTwoFactorForStaff`).
- KYC numbers are stored hashed with `Kyc:IdHashKey`; the full value is only revealed to staff on request and
  every reveal is written to the audit log.
- Uploaded documents are stored on disk under `Storage:LocalPath` with generated names. Put that volume on
  encrypted storage; for multiple instances, replace `LocalFileStorage` with object storage.
- The Ethereum and Bitcoin withdrawal paths are exercised by unit and integration tests against simulated
  gateways and by testnet-shaped configuration, but **they have not been run against a funded mainnet
  wallet**. Rehearse on testnet with small amounts before enabling live withdrawals, and keep the back office
  setting *Withdrawals → Manual review above (₦)* low at first.
- Nothing in this repository takes custody of customer funds automatically: hot wallets hold only what you
  fund them with, and every withdrawal above the threshold waits for staff approval.
