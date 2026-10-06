export module Dreamsleeve.Game.PhantomRecovery;
import std;
import Dreamsleeve.Client.Phantom.Types;

export namespace Dreamsleeve::Game::PhantomRecovery
{
  namespace P = Dreamsleeve::Client::Phantom;

  inline bool SameMask(const std::shared_ptr<const P::AlphaMask>& a, const std::shared_ptr<const P::AlphaMask>& b)
  {
    return a == b || (a && b && a->width == b->width && a->height == b->height && a->pixels == b->pixels);
  }

  // A fault belongs to one geometry slot. Its last complete payload stays in
  // snapshots while hidden; retry timing never stalls other slots or poses.
  class Mesh
  {
public:

    P::Bound       bound{{}, .01f};
    P::Deformation deformation;

    bool Ready(std::uint64_t now) const noexcept
    {
      return !fault_ || (fault_->reason != P::Failure::Stale && now >= retryAt_);
    }

    const std::optional<P::Error>& Fault() const noexcept
    {
      return fault_;
    }

    void Failed(P::Error error, std::uint64_t now)
    {
      fault_   = std::move(error);
      retryAt_ = now + (fault_->reason == P::Failure::Busy ? 50000 : 1000000);
    }

    void Recovered()
    {
      fault_.reset();
    }

    void Append(P::Snapshot& snapshot, P::NodeId node, std::uint32_t geometry, bool dynamic) const
    {
      if (fault_) snapshot.channels[node.value].hidden = true;
      // Hidden geometry has no visible extent. An old world-space bound must
      // not overflow position encoding after a teleport and poison this pose.
      snapshot.bounds.push_back(snapshot.channels[node.value].hidden ? P::Bound{snapshot.origin, 0} : bound);
      if (dynamic)
      {
        snapshot.deformations.push_back(deformation);
        snapshot.deformations.back().geometry = geometry;
      }
    }

private:

    std::optional<P::Error> fault_;
    std::uint64_t           retryAt_{};
  };

}
