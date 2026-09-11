using Crypton.Integrations.Blockchain;
using NBitcoin;

namespace Crypton.Tests.Unit;

public class HdKeysTests
{
    private const string Mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void Bitcoin_bip84_vector_from_mnemonic()
    {
        var root = new Mnemonic(Mnemonic, Wordlist.English).DeriveExtKey();
        var account = root.Derive(new KeyPath("m/84'/0'/0'")).Neuter();
        Assert.Equal("bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu", HdKeys.BitcoinAddress(account, 0, Network.Main));
        Assert.Equal("bc1qnjg0jd8228aq7egyzacy8cys3knf9xvrerkf9g", HdKeys.BitcoinAddress(account, 1, Network.Main));
    }

    [Fact]
    public void Bitcoin_zpub_is_accepted()
    {
        const string zpub = "zpub6rFR7y4Q2AijBEqTUquhVz398htDFrtymD9xYYfG1m4wAcvPhXNfE3EfH1r1ADqtfSdVCToUG868RvUUkgDKf31mGDtKsAYz2oz2AGutZYs";
        var account = HdKeys.ParseAccountXpub(zpub, Network.Main);
        Assert.Equal("bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu", HdKeys.BitcoinAddress(account, 0, Network.Main));
    }

    [Fact]
    public void Bitcoin_testnet_keys_derive_testnet_addresses_and_reject_mainnet_keys()
    {
        var root = new Mnemonic(Mnemonic, Wordlist.English).DeriveExtKey();
        var tpub = root.Derive(new KeyPath("m/84'/1'/0'")).Neuter().ToString(Network.TestNet);
        var account = HdKeys.ParseAccountXpub(tpub, Network.TestNet);
        var address = HdKeys.BitcoinAddress(account, 0, Network.TestNet);
        Assert.StartsWith("tb1q", address);
        Assert.True(HdKeys.IsValidBitcoinAddress(address, Network.TestNet));
        Assert.False(HdKeys.IsValidBitcoinAddress(address, Network.Main));

        var xpub = root.Derive(new KeyPath("m/84'/0'/0'")).Neuter().ToString(Network.Main);
        Assert.Throws<FormatException>(() => HdKeys.ParseAccountXpub(xpub, Network.TestNet));
    }

    [Fact]
    public void Ethereum_bip44_vector()
    {
        var root = new Mnemonic(Mnemonic, Wordlist.English).DeriveExtKey();
        var account = root.Derive(new KeyPath("m/44'/60'/0'")).Neuter();
        Assert.Equal("0x9858EfFD232B4033E47d90003D41EC34EcaEda94", HdKeys.EthereumAddress(account, 0));
    }

    [Theory]
    [InlineData("0x9858EfFD232B4033E47d90003D41EC34EcaEda94", true)]
    [InlineData("0x9858effd232b4033e47d90003d41ec34ecaeda94", true)]
    [InlineData("0x9858EfFD232B4033E47d90003D41EC34EcaEdA94", false)]
    [InlineData("0x9858EfFD232B4033E47d90003D41EC34EcaEda9", false)]
    [InlineData("9858EfFD232B4033E47d90003D41EC34EcaEda94", false)]
    public void Ethereum_address_validation(string address, bool valid) =>
        Assert.Equal(valid, HdKeys.IsValidEthereumAddress(address));
}
