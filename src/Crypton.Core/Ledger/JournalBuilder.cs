using Crypton.Core.Domain;

namespace Crypton.Core.Ledger;

public sealed record LedgerLeg(
    string AccountKey,
    AccountOwnerType OwnerType,
    Guid? UserId,
    string? SystemCode,
    string Asset,
    AccountKind Kind,
    decimal Amount);

/// <summary>Collects the signed legs of one balanced journal entry. Zero-amount legs are ignored.</summary>
public sealed class JournalBuilder
{
    private readonly List<LedgerLeg> _legs = [];

    public JournalBuilder(string type)
    {
        Type = type;
    }

    public string Type { get; }

    public string? IdempotencyKey { get; private set; }

    public string? ReferenceType { get; private set; }

    public Guid? ReferenceId { get; private set; }

    public Guid? UserId { get; private set; }

    public string? Description { get; private set; }

    public IReadOnlyList<LedgerLeg> Legs => _legs;

    public JournalBuilder User(Guid userId, string asset, AccountKind kind, decimal amount)
    {
        if (kind == AccountKind.System)
        {
            throw new ArgumentException("User legs cannot use the System account kind.", nameof(kind));
        }

        if (amount != 0)
        {
            _legs.Add(new LedgerLeg(LedgerAccount.UserKey(userId, asset, kind), AccountOwnerType.User, userId, null, asset, kind, amount));
        }

        return this;
    }

    public JournalBuilder System(string systemCode, string asset, decimal amount)
    {
        if (!SystemAccounts.All.Contains(systemCode))
        {
            throw new ArgumentException($"Unknown system account '{systemCode}'.", nameof(systemCode));
        }

        if (amount != 0)
        {
            _legs.Add(new LedgerLeg(LedgerAccount.SystemKey(systemCode, asset), AccountOwnerType.System, null, systemCode, asset, AccountKind.System, amount));
        }

        return this;
    }

    public JournalBuilder Reference(string referenceType, Guid referenceId)
    {
        ReferenceType = referenceType;
        ReferenceId = referenceId;
        return this;
    }

    public JournalBuilder ForUser(Guid userId)
    {
        UserId = userId;
        return this;
    }

    public JournalBuilder Idempotent(string key)
    {
        IdempotencyKey = key;
        return this;
    }

    public JournalBuilder Describe(string description)
    {
        Description = description.Length > 500 ? description[..500] : description;
        return this;
    }
}
