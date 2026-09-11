# Crypton Backend

Crypton is a .NET 10 backend foundation for an NGN-focused crypto trading and peer-to-peer marketplace platform. The codebase is being built around a PostgreSQL-backed, double-entry ledger so balances, escrow, fees, deposits, withdrawals, and trades can be represented as auditable journal entries.

## Current Status

This repository is at the foundation stage.

- The API currently exposes `GET /health` and returns `{ "status": "ok" }`.
- The core domain and service layer contains the planned exchange, wallet, fiat, compliance, and P2P workflows.
- EF Core is configured for PostgreSQL, but there are currently no committed migrations.
- `Crypton.Integrations` and `Crypton.KeyTool` are placeholders for provider adapters and operational tooling.
- The test project currently contains a smoke test; feature-level tests and HTTP endpoints are still to be added.

The practical next slice is to wire dependency injection, configuration, database migrations, authentication, and vertical API endpoints around the existing core services.

## Capabilities In The Core

### Trading and pricing

- Instant buy, sell, and crypto-to-crypto swap quotes against a platform treasury.
- Short-lived quotes with expiry, one-time consumption, client idempotency, spread, fees, precision checks, and liquidity checks.
- Pluggable price provider abstraction with a simulated provider, in-memory latest-price cache, and optional persisted price ticks.

### Ledger and money movement

- Zero-sum, per-asset double-entry journals.
- User accounts for available balances, withdrawal holds, and P2P reserves.
- System accounts for custody, treasury, fees, escrow, adjustments, network fees, and payment costs.
- Transaction-scoped postings, guarded atomic balance updates, idempotency keys, precision validation, and database non-negative-balance constraints.

### Wallets and fiat

- Crypto deposit addresses, confirmation tracking, deposits, withdrawals, chain cursors, and a development-only simulated chain model.
- NGN bank accounts, fiat deposits, and fiat withdrawals with provider references, fees, review states, and reversal/failure states.

### P2P marketplace

- Fixed- and floating-price ads, maker reserves, payment windows, escrow-backed orders, cancellation and expiry states.
- Disputes, evidence, resolution to buyer or seller, and post-order feedback.

### Identity and compliance

- ASP.NET Core Identity entities, refresh tokens, known devices, and audit logs.
- Tiered KYC submissions and documents, configurable transaction limits, and AML rules for deposits, withdrawals, trading, P2P activity, logins, and blocked or sanctioned addresses.

## Repository Layout

```text
src/
	Crypton.Api/            ASP.NET Core host and HTTP endpoints
	Crypton.Core/           Domain models, EF Core context, services, and business rules
	Crypton.Integrations/   External provider adapters (scaffold)
tests/
	Crypton.Tests/          xUnit tests
tools/
	Crypton.KeyTool/        Key and operational tooling (scaffold)
```

Important core areas include:

- `Data/`: PostgreSQL/EF Core configuration, context, and transaction helpers
- `Domain/`: identity, ledger, trading, wallet, fiat, KYC, AML, P2P, notification, and platform models
- `Ledger/`: journal construction and balance posting
- `Trading/`: quote and order execution
- `Pricing/`: price provider and cache abstractions
- `Kyc/`, `Security/`, and `Settings/`: limits, account guards, and runtime policy

## Technology

- .NET SDK `10.0.100` or a compatible .NET 10 SDK
- ASP.NET Core
- Entity Framework Core with PostgreSQL/Npgsql
- ASP.NET Core Identity
- JWT bearer authentication dependencies
- Scalar/OpenAPI dependencies
- xUnit v3 and ASP.NET Core integration-test dependencies
- NBitcoin, Nethereum, and MailKit dependencies reserved for integrations

## Local Setup

### Prerequisites

Install:

- .NET 10 SDK
- PostgreSQL 16 or newer
- The local `dotnet-ef` tool from `.config/dotnet-tools.json`

Create development databases and a login, for example:

```sql
CREATE USER crypton WITH PASSWORD 'change-me';
CREATE DATABASE crypton_dev OWNER crypton;
CREATE DATABASE crypton_test OWNER crypton;
```

The design-time EF factory defaults to:

```text
Host=localhost;Port=5432;Database=crypton_dev;Username=crypton;Password=crypton
```

For a different database, set `CRYPTON_DESIGN_CONNECTION` when running EF commands. Runtime configuration should use the application’s normal `ConnectionStrings` configuration once the API wiring is added; do not commit credentials.

### Restore, build, and test

```bash
dotnet tool restore
dotnet restore Crypton.slnx
dotnet build Crypton.slnx
dotnet test Crypton.slnx
```

Run the API:

```bash
dotnet run --project src/Crypton.Api/Crypton.Api.csproj
```

Then check the current endpoint:

```bash
curl http://localhost:5000/health
```

The actual port may differ depending on the ASP.NET Core launch profile.

## Database Migrations

No migrations are committed yet. Once the context and application registration are ready, create and apply the initial migration with:

```bash
dotnet ef migrations add InitialCreate \
	--project src/Crypton.Core/Crypton.Core.csproj \
	--startup-project src/Crypton.Api/Crypton.Api.csproj

dotnet ef database update \
	--project src/Crypton.Core/Crypton.Core.csproj \
	--startup-project src/Crypton.Api/Crypton.Api.csproj
```

Review generated migrations carefully before applying them to any shared environment.

## Design Principles

- Treat the ledger as the source of truth for balances and movement of value.
- Require database transactions for every ledger posting.
- Make external callbacks and retried commands idempotent.
- Keep provider-specific behavior behind integration interfaces.
- Enforce precision, limits, compliance decisions, and available-balance checks at the service boundary and database boundary where possible.
- Use the simulated chain and price provider only for development or demo workflows.

## Development Notes

The solution uses central package version management in `Directory.Packages.props`, targets `net10.0`, enables nullable reference types, and uses snake_case naming for PostgreSQL objects. Keep business rules in `Crypton.Core`; the API project should remain a composition and transport layer.