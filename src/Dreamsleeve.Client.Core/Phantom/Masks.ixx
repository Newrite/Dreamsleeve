export module Dreamsleeve.Client.Phantom.Masks;
import std;
import Dreamsleeve.Client.Phantom.Types;

export namespace Dreamsleeve::Client::Phantom
{

  // Immutable masks are appearance resources shared by meshes. Equality
  // includes dimensions and ALL pixels; the hash is only a lookup accelerator.
  class AlphaMaskPool
  {
public:

    explicit AlphaMaskPool(Limits limits = {}) : limits_(limits) {}

    Result<std::shared_ptr<const AlphaMask>> Intern(std::shared_ptr<const AlphaMask> mask)
    {
      if (!mask) return std::unexpected(Error{Failure::InvalidMask, "mask.missing"});
      if (known_.contains(mask.get())) return mask;
      if (
        !mask->width || !mask->height || mask->width > limits_.maskDimension || mask->height > limits_.maskDimension ||
        std::uint64_t(mask->width) * mask->height != mask->pixels.size() || mask->pixels.size() > limits_.maskBytes)
        return std::unexpected(Error{Failure::InvalidMask, "mask.shape"});
      std::uint64_t hash = 14695981039346656037ULL;
      for (auto pixel : mask->pixels)
        hash = (hash ^ pixel) * 1099511628211ULL;
      hash ^= (std::uint64_t(mask->width) << 32) | mask->height;
      if (const auto bucket = masks_.find(hash); bucket != masks_.end())
        for (const auto& saved : bucket->second)
          if (saved->width == mask->width && saved->height == mask->height && saved->pixels == mask->pixels) return saved;
      if (known_.size() >= limits_.geometry || mask->pixels.size() > limits_.maskBytes - bytes_)
        return std::unexpected(Error{Failure::LimitExceeded, "mask.unique-bytes"});
      bytes_ += mask->pixels.size();
      known_.insert(mask.get());
      masks_[hash].push_back(mask);
      return mask;
    }

    std::uint64_t Bytes() const noexcept
    {
      return bytes_;
    }

private:

    Limits                                                                           limits_;
    std::uint64_t                                                                    bytes_{};
    std::unordered_set<const AlphaMask*>                                             known_;
    std::unordered_map<std::uint64_t, std::vector<std::shared_ptr<const AlphaMask>>> masks_;
  };

}
