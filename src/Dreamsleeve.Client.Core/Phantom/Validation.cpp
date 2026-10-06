import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Client.Phantom.Nif;

namespace Dreamsleeve::Client::Phantom
{

  Result<ValidatedAsset> ValidatedAsset::Parse(Asset asset, const Limits& limits)
  {
    auto layout = Nif::Inspect(asset.nif, limits);
    if (!layout) return std::unexpected(layout.error());
    const auto memory = sizeof(Asset) + asset.nif.capacity() + sizeof(NativeLayout) + layout->nodes.capacity() * sizeof(NativeNode) +
                        (layout->bounds.capacity() + layout->requiredChannels.capacity()) * sizeof(std::uint32_t);
    return ValidatedAsset(std::move(asset), std::move(*layout), memory);
  }

}
