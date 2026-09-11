using Crypton.Api.Contracts;
using Crypton.Api.Jobs;
using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Domain;
using Crypton.Core.Notifications;
using Crypton.Core.Wallets;
using Crypton.Integrations.Blockchain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Crypton.Api.Controllers;

public sealed class DevOptions
{
    public const string Section = "Dev";

    /// <summary>Enables /api/dev endpoints (simulated deposits, job triggers). Never enable in production.</summary>
    public bool EnableEndpoints { get; set; }
}

/// <summary>Development and demo helpers. Only available with simulated blockchain mode and Dev:EnableEndpoints.</summary>
[Route("api/dev")]
public sealed class DevController(
    IOptions<DevOptions> dev,
    IOptions<BlockchainOptions> blockchain,
    IWebHostEnvironment environment,
    ChainGatewayRegistry chains,
    DepositAddressService addresses,
    AssetCatalog assets,
    JobRunner jobs) : ApiControllerBase
{
    [HttpPost("simulate/crypto-deposit")]
    public async Task<CryptoDepositDto> SimulateDeposit(SimulateDepositRequest request, CancellationToken ct)
    {
        EnsureEnabled();
        var asset = await assets.GetAsync(request.Asset, ct);
        if (asset.IsFiat || asset.Network is null)
        {
            throw AppException.Validation("Choose a crypto asset.");
        }

        if (request.Amount <= 0 || !MoneyMath.HasMaxDecimals(request.Amount, asset.Precision))
        {
            throw AppException.Validation($"Amount must be positive with at most {asset.Precision} decimals.");
        }

        var address = await addresses.GetOrCreateAsync(CurrentUserId, asset.Code, ct);
        if (chains.Get(asset.Network) is not SimulatedChainGateway simulator)
        {
            throw AppException.NotFound("Page");
        }

        var tx = await simulator.CreateInboundAsync(asset.Code, address.Address, request.Amount, ct);
        return new CryptoDepositDto(tx.Id, asset.Code, asset.Network, tx.TxHash, tx.Amount, 0, asset.RequiredConfirmations, CryptoDepositStatus.Pending, null, tx.CreatedAt, null);
    }

    [HttpPost("jobs/{name}")]
    public async Task<IActionResult> RunJob(string name, CancellationToken ct)
    {
        EnsureEnabled();
        return await jobs.RunOnceAsync(name.Replace('-', ':'), ct) ? NoContent() : NotFound();
    }

    private void EnsureEnabled()
    {
        if (!dev.Value.EnableEndpoints || !blockchain.Value.IsSimulated || environment.IsProduction())
        {
            throw AppException.NotFound("Page");
        }
    }
}
