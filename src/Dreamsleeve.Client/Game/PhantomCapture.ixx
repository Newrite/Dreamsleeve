export module Dreamsleeve.Game.PhantomCapture;

import std;

// Scene-independent rules for sampling a fixed appearance from a changing
// live hierarchy. No stored child order or bone-array pointers are required.
export namespace PhantomCapture
{

  struct Space
  {
    std::uint32_t cell{};
    std::uint32_t world{};

    bool Contains(Space current) const
    {
      return cell && current.cell && (world ? current.world == world : !current.world && current.cell == cell);
    }
  };

  // Camera culling is not authored visibility. Keep the last third-person
  // visibility while the game hides the third-person model for its camera.
  struct Visibility
  {
    bool hidden{};

    bool Sample(bool present, bool culled, bool firstPerson)
    {
      if (present && !firstPerson) hidden = culled;
      return !present || hidden;
    }
  };

  struct Attachment
  {
    bool present{};
    bool hidden{};
  };

  // VR also culls by a world AABB. A box around the replay sphere is
  // conservative even when the captured geometry rotates between samples.
  template <class Bound, class Box>
  void OcclusionBox(const Bound& bound, Box& box)
  {
    box.center        = bound.center;
    const auto radius = std::max(0.0f, bound.radius);
    box.halfExtents   = {radius, radius, radius};
  }

  // Replay keeps its original hierarchy even when live equipment is reparented.
  // Its container bounds must enclose replay children, not the now-empty live
  // sheath node. A zero-radius bound is empty, as in NiNode::UpdateWorldBound.
  template <class Bound>
  void Enclose(Bound& into, const Bound& other)
  {
    if (other.radius <= 0.0f) return;
    if (into.radius <= 0.0f)
    {
      into = other;
      return;
    }
    const auto delta    = other.center - into.center;
    const auto distance = std::sqrt(delta.x * delta.x + delta.y * delta.y + delta.z * delta.z);
    if (distance + other.radius <= into.radius) return;
    if (distance + into.radius <= other.radius)
    {
      into = other;
      return;
    }
    const auto radius = (distance + into.radius + other.radius) * 0.5f;
    if (distance > 0.0f) into.center = into.center + delta * ((radius - into.radius) / distance);
    into.radius = radius;
  }

  template <class Node, class Parent, class Hidden>
  Attachment Locate(Node* root, Node* object, Parent parent, Hidden hidden)
  {
    bool culled = false;
    for (std::size_t depth = 0; object && depth < 4096; ++depth, object = parent(object))
    {
      if (object == root) return {true, culled};
      culled |= hidden(object);
    }
    return {};
  }

  template <class Bone, class Name>
  std::optional<std::size_t> FindBone(std::span<const Bone> bones, std::string_view name, std::size_t hint, Name getName)
  {
    if (hint < bones.size() && getName(bones[hint]) == name) return hint;
    std::optional<std::size_t> found;
    for (std::size_t i = 0; i < bones.size(); ++i)
      if (getName(bones[i]) == name)
      {
        if (found) return std::nullopt;  // Ambiguous names cannot safely rebind.
        found = i;
      }
    return found;
  }

}
