using Hookline.Core;

namespace Hookline.Core.Tests;

public class IdsTests
{
    [Fact]
    public void An_id_is_its_prefix_and_128_random_bits_in_hex()
    {
        var id = Ids.New(Ids.Event);

        Assert.Matches("^evt_[0-9a-f]{32}$", id);
    }

    [Fact]
    public void Ids_dont_repeat()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Ids.New(Ids.Delivery)).ToHashSet();

        Assert.Equal(1000, ids.Count);
    }
}
