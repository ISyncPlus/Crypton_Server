using Crypton.Core.Aml;
using Crypton.Core.Domain;
using Crypton.Integrations.Blockchain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Web3;

namespace Crypton.Integrations.Compliance;

/// <summary>
/// Screens Ethereum destination addresses against the free Chainalysis sanctions oracle contract
/// (OFAC-listed addresses) on Ethereum mainnet.
/// </summary>
public sealed class ChainalysisOracleScreening(IOptions<SanctionsOracleOptions> options, IMemoryCache cache) : IAddressScreeningProvider
{
    public string Name => "chainalysis-oracle";

    public async Task<bool?> IsSanctionedAsync(string network, string address, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.RpcUrl) || network != Networks.Ethereum || !HdKeys.IsValidEthereumAddress(address))
        {
            return null;
        }

        var key = $"sanctions:{address.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        var web3 = new Web3(o.RpcUrl);
        var handler = web3.Eth.GetContractQueryHandler<IsSanctionedFunction>();
        var result = await handler.QueryAsync<bool>(o.OracleAddress, new IsSanctionedFunction { Address = HdKeys.NormalizeEthereumAddress(address) });
        cache.Set(key, result, TimeSpan.FromHours(6));
        return result;
    }

    [Function("isSanctioned", "bool")]
    public sealed class IsSanctionedFunction : FunctionMessage
    {
        [Parameter("address", "addr", 1)]
        public string Address { get; set; } = "";
    }
}
