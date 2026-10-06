import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Client.Phantom.Masks;

namespace Dreamsleeve::Client::Phantom
{
  namespace
  {

    bool Finite(const Vec3& v)
    {
      return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
    }

    bool Finite(const Transform& t)
    {
      const auto& q    = t.rotation;
      const auto  norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
      return Finite(t.position) && std::isfinite(t.scale) && t.scale > 0 && t.scale <= 1024 && std::isfinite(norm) &&
             std::abs(norm - 1) < 0.01f;
    }

    bool Finite(const Bound& b)
    {
      return Finite(b.center) && std::isfinite(b.radius) && b.radius >= 0 && b.radius <= 100000;
    }

  }

  Result<void> ValidateGeometry(const Geometry& mesh, std::size_t nodeCount, const Limits& limits)
  {
    const auto error = [](Failure reason, std::string field) -> Result<void> {
      return std::unexpected(Error{reason, std::move(field)});
    };
    if (
      mesh.vertices.empty() || mesh.vertices.size() > 65535 || mesh.indices.empty() || mesh.indices.size() % 3 != 0 ||
      mesh.node.value >= nodeCount)
      return error(Failure::InvalidGeometry, "geometry.counts");
    for (const auto index : mesh.indices)
      if (index >= mesh.vertices.size()) return error(Failure::InvalidLink, "geometry.index");
    if (mesh.skin)
    {
      const auto& skin = *mesh.skin;
      if (skin.root.value >= nodeCount || skin.bones.empty() || skin.bones.size() > limits.bonesPerSkin || !Finite(skin.worldToSkin))
        return error(Failure::InvalidSkin, "skin.root");
      for (const auto& bone : skin.bones)
        if (bone.node.value >= nodeCount || !Finite(bone.bind) || !Finite(bone.bound)) return error(Failure::InvalidSkin, "skin.bone");
    }
    for (const auto& vertex : mesh.vertices)
    {
      if (
        !Finite(vertex.position) || !Finite(vertex.normal) || !Finite(vertex.tangent) || !std::isfinite(vertex.u) ||
        !std::isfinite(vertex.v))
        return error(Failure::InvalidNumber, "vertex");
      if (!vertex.ValidWeights(mesh.skin ? mesh.skin->bones.size() : 0)) return error(Failure::InvalidSkin, "vertex.weight");
    }
    return {};
  }

  Result<ValidatedAsset> ValidatedAsset::Parse(Asset asset, const Limits& limits)
  {
    const auto error = [](Failure reason, std::string field) -> Result<ValidatedAsset> {
      return std::unexpected(Error{reason, std::move(field)});
    };
    if (asset.nodes.empty() || asset.nodes.size() > limits.nodes || asset.geometry.empty() || asset.geometry.size() > limits.geometry)
      return error(Failure::LimitExceeded, "asset.counts");
    for (std::size_t i = 0; i < asset.nodes.size(); ++i)
    {
      const auto& node = asset.nodes[i];
      if ((i == 0 && node.parent.value != NoNode) || (i != 0 && node.parent.value >= i)) return error(Failure::InvalidLink, "node.parent");
      if (!Finite(node.local)) return error(Failure::InvalidNumber, "node.transform");
    }
    std::uint64_t vertices = 0, bytes = asset.nodes.size() * 40ULL;
    AlphaMaskPool masks(limits);
    for (auto& mesh : asset.geometry)
    {
      vertices += mesh.vertices.size();
      bytes    += GeometryBytes(mesh);
      if (vertices > limits.vertices || bytes > limits.assetBytes) return error(Failure::LimitExceeded, "asset.geometry-budget");
      auto valid = ValidateGeometry(mesh, asset.nodes.size(), limits);
      if (!valid) return std::unexpected(valid.error());
      if (mesh.mask)
      {
        auto mask = masks.Intern(mesh.mask);
        if (!mask) return std::unexpected(mask.error());
        mesh.mask = *mask;
      }
      if (bytes > limits.assetBytes) return error(Failure::LimitExceeded, "asset.bytes");
    }
    return ValidatedAsset(std::move(asset), bytes);
  }

}
