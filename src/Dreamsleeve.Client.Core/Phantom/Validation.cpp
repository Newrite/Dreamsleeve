import std;
import Dreamsleeve.Client.Phantom.Types;

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
    std::uint64_t vertices = 0, masks = 0, bytes = asset.nodes.size() * 40ULL;
    for (const auto& mesh : asset.geometry)
    {
      vertices += mesh.vertices.size();
      bytes    += mesh.vertices.size() * 80ULL + mesh.indices.size() * 2ULL;
      if (
        vertices > limits.vertices || bytes > limits.assetBytes || mesh.vertices.empty() || mesh.vertices.size() > 65535 ||
        mesh.indices.empty() || mesh.indices.size() % 3 != 0 || mesh.node.value >= asset.nodes.size())
        return error(Failure::InvalidGeometry, "geometry.counts");
      for (const auto index : mesh.indices)
        if (index >= mesh.vertices.size()) return error(Failure::InvalidLink, "geometry.index");
      if (mesh.skin)
      {
        const auto& skin  = *mesh.skin;
        bytes            += skin.bones.size() * 60ULL;
        if (
          skin.root.value >= asset.nodes.size() || skin.bones.empty() || skin.bones.size() > limits.bonesPerSkin ||
          !Finite(skin.worldToSkin))
          return error(Failure::InvalidSkin, "skin.root");
        for (const auto& bone : skin.bones)
          if (bone.node.value >= asset.nodes.size() || !Finite(bone.bind) || !Finite(bone.bound))
            return error(Failure::InvalidSkin, "skin.bone");
      }
      for (const auto& vertex : mesh.vertices)
      {
        if (
          !Finite(vertex.position) || !Finite(vertex.normal) || !Finite(vertex.tangent) || !std::isfinite(vertex.u) ||
          !std::isfinite(vertex.v))
          return error(Failure::InvalidNumber, "vertex");
        float sum = 0;
        for (std::size_t i = 0; i < 4; ++i)
        {
          const auto weight = vertex.weights[i];
          if (
            !std::isfinite(weight) || weight < 0 || weight > 1 ||
            (weight > 0 && (!mesh.skin || vertex.bones[i] >= mesh.skin->bones.size())))
            return error(Failure::InvalidSkin, "vertex.weight");
          sum += weight;
        }
        if (mesh.skin && std::abs(sum - 1) > 0.01f) return error(Failure::InvalidSkin, "vertex.weights");
      }
      if (mesh.mask)
      {
        const auto& mask  = *mesh.mask;
        masks            += mask.pixels.size();
        bytes            += mask.pixels.size();
        if (
          !mask.width || !mask.height || mask.width > limits.maskDimension || mask.height > limits.maskDimension ||
          static_cast<std::uint64_t>(mask.width) * mask.height != mask.pixels.size() || masks > limits.maskBytes)
          return error(Failure::InvalidMask, "geometry.mask");
      }
      if (bytes > limits.assetBytes) return error(Failure::LimitExceeded, "asset.bytes");
    }
    return ValidatedAsset(std::move(asset), bytes);
  }

}
