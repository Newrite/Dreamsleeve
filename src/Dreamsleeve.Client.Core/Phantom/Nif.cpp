import std;
import Dreamsleeve.Client.Phantom.Nif;

namespace Dreamsleeve::Client::Phantom::Nif
{
  namespace
  {

    std::unexpected<Error> Fail(std::string_view field)
    {
      return std::unexpected(Error{Failure::InvalidFormat, "nif." + std::string(field)});
    }

    struct Reader
    {
      std::span<const std::uint8_t> bytes;
      std::size_t                   at{};
      Result<void>                  status;

      void Reject(std::string_view field)
      {
        if (status) status = Fail(field);
      }

      std::span<const std::uint8_t> Take(std::size_t size)
      {
        if (!status) return {};
        if (size > bytes.size() - at)
        {
          Reject("truncated");
          return {};
        }
        const auto out  = bytes.subspan(at, size);
        at             += size;
        return out;
      }

      template <class T>
      T Get()
      {
        T          out{};
        const auto data = Take(sizeof(T));
        if (status) std::memcpy(&out, data.data(), sizeof(T));
        return out;
      }

      std::uint32_t Count(std::uint32_t maximum, std::size_t stride)
      {
        const auto count = Get<std::uint32_t>();
        if (!status) return 0;
        if (count > maximum || count > (bytes.size() - at) / stride)
        {
          Reject("count");
          return 0;
        }
        return count;
      }

      bool Bool()
      {
        const auto value = Get<std::uint8_t>();
        if (value > 1) Reject("bool");
        return value != 0;
      }

      void Floats(std::size_t count)
      {
        for (std::size_t i = 0; status && i < count; ++i)
          if (!std::isfinite(Get<float>())) Reject("number");
      }

      std::string Text(std::uint32_t length)
      {
        if (length > 4096) Reject("string-length");
        const auto data = Take(length);
        if (std::ranges::find(data, 0) != data.end()) Reject("string-nul");
        if (!status || data.empty()) return {};
        return {reinterpret_cast<const char*>(data.data()), data.size()};
      }

      Result<void> End()
      {
        if (at != bytes.size()) Reject("block-length");
        return status;
      }
    };

    enum class Kind
    {
      Node,
      Shape,
      Dynamic,
      SubIndex,
      Lighting,
      Alpha,
      Textures,
      Skin,
      Dismember,
      SkinData,
      Partition
    };
    constexpr std::array<std::string_view, 11> Names{
        "NiNode",
        "BSTriShape",
        "BSDynamicTriShape",
        "BSSubIndexTriShape",
        "BSLightingShaderProperty",
        "NiAlphaProperty",
        "BSShaderTextureSet",
        "NiSkinInstance",
        "BSDismemberSkinInstance",
        "NiSkinData",
        "NiSkinPartition"
    };

    bool Shape(Kind kind)
    {
      return kind == Kind::Shape || kind == Kind::Dynamic || kind == Kind::SubIndex;
    }

    bool Scene(Kind kind)
    {
      return kind == Kind::Node || Shape(kind);
    }

    bool Skin(Kind kind)
    {
      return kind == Kind::Skin || kind == Kind::Dismember;
    }

    struct Block
    {
      Kind                          kind;
      std::span<const std::uint8_t> bytes;
      std::vector<std::uint32_t>    children;
      std::vector<std::uint32_t>    bones;

      std::uint32_t                 skin{NoNode};
      std::uint32_t                 data{NoNode};
      std::uint32_t                 partition{NoNode};
      std::uint32_t                 root{NoNode};

      std::uint32_t                 vertices{};
      std::uint32_t                 boneCount{};
      std::uint32_t                 partitions{};
      std::uint32_t                 maximumVertex{};
      std::uint32_t                 maximumBone{};
      bool                          weights{};
    };

    class Parser
    {
      const Limits&      limits;
      std::vector<Block> blocks;
      std::uint32_t      strings{};
      Layout             result;

      Result<std::uint32_t> Ref(Reader& r, auto accepts, bool optional = false)
      {
        const auto id = r.Get<std::uint32_t>();
        if (!r.status) return std::unexpected(r.status.error());
        if (optional && id == NoNode) return id;
        if (!(id < blocks.size() && accepts(blocks[id].kind))) return Fail("reference");
        return id;
      }

      Result<std::uint32_t> Ref(Reader& r, Kind kind, bool optional = false)
      {
        return Ref(r, [kind](Kind value) { return value == kind; }, optional);
      }

      Result<void> Net(Reader& r)
      {
        const auto name = r.Get<std::uint32_t>();
        if (!(name == NoNode || name < strings)) return Fail("name");
        if (!(r.Get<std::uint32_t>() == 0)) return Fail("extra-data");
        if (!(r.Get<std::uint32_t>() == NoNode)) return Fail("controller");
        return r.status;
      }

      Result<void> AV(Reader& r)
      {
        if (auto checked = Net(r); !checked) return std::unexpected(checked.error());
        r.Get<std::uint32_t>();  // native flags
        r.Floats(12);
        const auto scale = r.Get<float>();
        if (!(std::isfinite(scale) && scale > 0 && scale <= 1024)) return Fail("scale");
        if (!(r.Get<std::uint32_t>() == NoNode)) return Fail("collision");
        return r.status;
      }

      Result<void> Bound(Reader& r)
      {
        r.Floats(3);
        const auto radius = r.Get<float>();
        if (!(std::isfinite(radius) && radius >= 0 && radius <= 100000)) return Fail("bound");
        return r.status;
      }

      Result<std::uint32_t> Descriptor(std::uint64_t descriptor)
      {
        const auto stride = static_cast<std::uint32_t>(descriptor & 15) * 4;
        if (!(stride > 0 && stride <= 60)) return Fail("vertex-stride");
        // Attribute footprints must fit the native buffer. Packed bytes stay
        // native; there is no vertex conversion or renderer here.
        constexpr std::array<unsigned, 9> size{8, 4, 4, 4, 4, 4, 12, 4, 4};
        for (unsigned i = 1; i < size.size(); ++i)
          if (descriptor & (1ULL << (44 + i)))
          {
            const auto offset = (descriptor >> (4 * i + 2)) & 0x3c;
            if (!(offset + size[i] <= stride)) return Fail("vertex-attribute");
          }
        if (!((descriptor >> 55) == 0)) return Fail("vertex-flags");
        return stride;
      }

      Result<void> Indices(Reader& r, std::size_t count, std::uint32_t vertices)
      {
        for (std::size_t i = 0; i < count; ++i)
          if (!(r.Get<std::uint16_t>() < vertices)) return Fail("vertex-index");
        return r.status;
      }

      Result<void> Geometry(Reader& r, Block& b)
      {
        if (auto checked = AV(r); !checked) return std::unexpected(checked.error());
        if (auto checked = Bound(r); !checked) return std::unexpected(checked.error());
        auto skin = Ref(r, Skin, true);
        if (!skin) return std::unexpected(skin.error());
        b.skin = *skin;
        if (auto link = Ref(r, Kind::Lighting); !link) return std::unexpected(link.error());
        if (auto link = Ref(r, Kind::Alpha, true); !link) return std::unexpected(link.error());

        const auto descriptor   = r.Get<std::uint64_t>();
        const auto strideResult = Descriptor(descriptor);
        if (!strideResult) return std::unexpected(strideResult.error());
        const auto stride    = *strideResult;
        const auto triangles = r.Get<std::uint16_t>();
        b.vertices           = r.Get<std::uint16_t>();
        if (!((b.vertices > 0 && triangles > 0) || b.skin != NoNode)) return Fail("empty-shape");
        const auto size = r.Get<std::uint32_t>();
        if (size)
        {
          if (!(size == std::uint64_t(b.vertices) * stride + std::uint64_t(triangles) * 6)) return Fail("shape-buffer-length");
          r.Take(std::size_t(b.vertices) * stride);
          if (auto checked = Indices(r, std::size_t(triangles) * 3, b.vertices); !checked) return std::unexpected(checked.error());
          result.vertexBytes += size;
        }
        else if (!(b.skin != NoNode))
          return Fail("missing-shape-buffer");
        if (!(r.Get<std::uint32_t>() == 0)) return Fail("particle-data");

        if (b.kind == Kind::Dynamic)
        {
          if (!(r.Get<std::uint32_t>() == b.vertices * 16)) return Fail("dynamic-size");
          r.Floats(std::size_t(b.vertices) * 4);
          result.vertexBytes += std::uint64_t(b.vertices) * 16;
        }

        if (b.kind == Kind::SubIndex)
        {
          const auto segments = r.Count(65535, 9);
          for (std::uint32_t i = 0; i < segments; ++i)
          {
            r.Get<std::uint8_t>();
            const auto start = r.Get<std::uint32_t>(), count = r.Get<std::uint32_t>();
            if (!(start <= triangles * 3u && count <= triangles && start + std::uint64_t(count) * 3 <= triangles * 3u))
              return Fail("segment");
          }
        }
        return r.status;
      }

      Result<void> Partition(Reader& r, Block& b)
      {
        b.partitions = r.Count(4096, 12);
        if (!(b.partitions > 0)) return Fail("partitions");
        const auto size = r.Get<std::uint32_t>(), stride = r.Get<std::uint32_t>();
        const auto descriptor     = r.Get<std::uint64_t>();
        auto       expectedStride = Descriptor(descriptor);
        if (!expectedStride) return std::unexpected(expectedStride.error());
        if (!(stride == *expectedStride && size && size % stride == 0)) return Fail("partition-buffer");
        b.vertices = size / stride;
        if (!(b.vertices <= 65535)) return Fail("partition-vertices");
        r.Take(size);
        result.vertexBytes += size;

        for (std::uint32_t p = 0; p < b.partitions; ++p)
        {
          const auto vertices = r.Get<std::uint16_t>(), triangles = r.Get<std::uint16_t>(), bones = r.Get<std::uint16_t>();
          if (!(vertices <= b.vertices && bones && bones <= 256)) return Fail("partition-counts");
          if (!(r.Get<std::uint16_t>() == 0)) return Fail("strips");
          const auto weights = r.Get<std::uint16_t>();
          if (!(weights == 4)) return Fail("partition-weights");
          for (unsigned i = 0; i < bones; ++i)
            b.maximumBone = std::max(b.maximumBone, std::uint32_t(r.Get<std::uint16_t>()));
          if (r.Bool())
            if (auto checked = Indices(r, vertices, b.vertices); !checked) return std::unexpected(checked.error());
          if (r.Bool())
            for (std::size_t i = 0; i < std::size_t(vertices) * weights; ++i)
            {
              const auto weight = r.Get<float>();
              if (!(std::isfinite(weight) && weight >= 0 && weight <= 1)) return Fail("weight");
            }
          if (r.Bool())
            if (auto checked = Indices(r, std::size_t(triangles) * 3, b.vertices); !checked) return std::unexpected(checked.error());
          if (r.Bool())
            for (std::size_t i = 0; i < std::size_t(vertices) * weights; ++i)
              if (!(r.Get<std::uint8_t>() < bones)) return Fail("palette-index");
          r.Get<std::uint8_t>();
          r.Bool();
          auto partitionStride = Descriptor(r.Get<std::uint64_t>());
          if (!partitionStride) return std::unexpected(partitionStride.error());
          if (!(*partitionStride == stride)) return Fail("partition-descriptor");
          if (auto checked = Indices(r, std::size_t(triangles) * 3, b.vertices); !checked) return std::unexpected(checked.error());
        }
        return r.status;
      }

      Result<void> ReadBlock(Block& b)
      {
        Reader r{b.bytes};
        if (Shape(b.kind))
        {
          if (auto checked = Geometry(r, b); !checked) return std::unexpected(checked.error());
        }
        else
          switch (b.kind)
          {
            case Kind::Node: {
              if (auto checked = AV(r); !checked) return std::unexpected(checked.error());
              const auto children = r.Count(limits.nodes, 4);
              for (std::uint32_t i = 0; i < children; ++i)
              {
                const auto child = Ref(r, Scene, true);
                if (!child) return std::unexpected(child.error());
                if (*child != NoNode) b.children.push_back(*child);
              }
              if (!(r.Get<std::uint32_t>() == 0)) return Fail("effects");
              break;
            }
            case Kind::Lighting: {
              const auto type = r.Get<std::uint32_t>();
              if (!(type <= 19)) return Fail("lighting-type");
              if (auto checked = Net(r); !checked) return std::unexpected(checked.error());
              r.Get<std::uint32_t>();
              r.Get<std::uint32_t>();
              r.Floats(4);
              if (auto link = Ref(r, Kind::Textures); !link) return std::unexpected(link.error());
              r.Floats(4);
              if (!(r.Get<std::uint32_t>() <= 3)) return Fail("texture-clamp");
              r.Floats(9);
              switch (type)
              {
                case 1:
                  r.Floats(1);
                  break;
                case 5:
                case 6:
                  r.Floats(3);
                  break;
                case 7:
                  r.Floats(2);
                  break;
                case 11:
                  r.Floats(5);
                  break;
                case 14:
                  r.Floats(4);
                  break;
                case 16:
                  r.Floats(7);
                  break;
                default:
                  break;
              }
              break;
            }
            case Kind::Alpha:
              if (auto checked = Net(r); !checked) return std::unexpected(checked.error());
              r.Get<std::uint16_t>();
              r.Get<std::uint8_t>();
              break;
            case Kind::Textures: {
              const auto count = r.Count(9, 4);
              for (std::uint32_t i = 0; i < count; ++i)
              {
                const auto path = r.Text(r.Get<std::uint32_t>());
                // The ghost appearance supplies its own local material. Peer
                // paths must never reach BSResource or the OS filesystem.
                if (!(path.empty())) return Fail("external-texture");
              }
              break;
            }
            case Kind::Skin:
            case Kind::Dismember: {
              auto data = Ref(r, Kind::SkinData);
              if (!data) return std::unexpected(data.error());
              b.data         = *data;
              auto partition = Ref(r, Kind::Partition);
              if (!partition) return std::unexpected(partition.error());
              b.partition = *partition;
              auto root   = Ref(r, Kind::Node);
              if (!root) return std::unexpected(root.error());
              b.root      = *root;
              b.boneCount = r.Count(limits.nodes, 4);
              if (!(b.boneCount > 0)) return Fail("skin-bones");
              for (std::uint32_t i = 0; i < b.boneCount; ++i)
              {
                auto bone = Ref(r, Kind::Node);
                if (!bone) return std::unexpected(bone.error());
                b.bones.push_back(*bone);
              }
              if (b.kind == Kind::Dismember)
              {
                b.partitions = r.Count(4096, 4);
                r.Take(std::size_t(b.partitions) * 4);
              }
              break;
            }
            case Kind::SkinData: {
              r.Floats(13);
              b.boneCount = r.Count(limits.nodes, 70);
              b.weights   = r.Bool();
              for (std::uint32_t i = 0; i < b.boneCount; ++i)
              {
                r.Floats(13);
                if (auto checked = Bound(r); !checked) return std::unexpected(checked.error());
                const auto vertices = r.Get<std::uint16_t>();
                if (b.weights)
                  for (std::uint32_t v = 0; v < vertices; ++v)
                  {
                    b.maximumVertex   = std::max(b.maximumVertex, std::uint32_t(r.Get<std::uint16_t>()));
                    const auto weight = r.Get<float>();
                    if (!(std::isfinite(weight) && weight >= 0 && weight <= 1)) return Fail("skin-weight");
                  }
              }
              break;
            }
            case Kind::Partition:
              if (auto checked = Partition(r, b); !checked) return std::unexpected(checked.error());
              break;
            default:
              if (!(false)) return Fail("class");
          }
        return r.End();
      }

      Result<Layout> BuildLayout(const std::uint32_t& root, std::uint32_t count)
      {
        std::vector<std::uint32_t> ordinal(blocks.size(), NoNode);
        const auto                 visit = [&](auto&& self, std::uint32_t id, std::uint32_t parent, unsigned depth) -> Result<void> {
          if (!(depth <= 256 && result.nodes.size() < limits.nodes && ordinal[id] == NoNode)) return Fail("tree");
          const auto index = static_cast<std::uint32_t>(result.nodes.size());
          ordinal[id]      = index;
          result.nodes.push_back({id, parent, Shape(blocks[id].kind)});
          if (Shape(blocks[id].kind)) result.bounds.push_back(index);
          for (const auto child : blocks[id].children)
            if (auto visited = self(self, child, index, depth + 1); !visited) return std::unexpected(visited.error());
          return {};
        };
        if (auto visited = visit(visit, root, NoNode, 0); !visited) return std::unexpected(visited.error());
        if (!(!result.bounds.empty())) return Fail("no-geometry");

        std::set<std::uint32_t> channels{0};
        for (std::uint32_t i = 0; i < blocks.size(); ++i)
        {
          const auto& b = blocks[i];
          if (Scene(b.kind))
            if (!(ordinal[i] != NoNode)) return Fail("unreachable-scene");
          if (Shape(b.kind))
          {
            channels.insert(ordinal[i]);
            if (b.skin != NoNode)
            {
              const auto& skin      = blocks[b.skin];
              const auto& data      = blocks[skin.data];
              const auto& partition = blocks[skin.partition];
              if (!(skin.boneCount == data.boneCount && partition.maximumBone < skin.boneCount)) return Fail("skin-counts");
              if (!((b.vertices == 0 || partition.vertices == b.vertices) && (!data.weights || data.maximumVertex < partition.vertices)))
                return Fail("skin-vertices");
              if (skin.kind == Kind::Dismember)
                if (!(skin.partitions == partition.partitions)) return Fail("dismember-count");
              if (!(ordinal[skin.root] != NoNode)) return Fail("skin-root");
              channels.insert(ordinal[skin.root]);
              for (const auto bone : skin.bones)
              {
                if (!(ordinal[bone] != NoNode)) return Fail("skin-bone");
                channels.insert(ordinal[bone]);
              }
            }
          }
        }

        result.blocks = count;
        result.requiredChannels.assign(channels.begin(), channels.end());
        return std::move(result);
      }

  public:

      explicit Parser(const Limits& value) : limits(value) {}

      Result<Layout> Read(std::span<const std::uint8_t> bytes)
      {
        if (!(!bytes.empty() && bytes.size() <= limits.assetBytes)) return Fail("size");
        Reader                     r{bytes};
        constexpr std::string_view header    = "Gamebryo File Format, Version 20.2.0.7\n";
        const auto                 signature = r.Take(header.size());
        if (!(std::equal(signature.begin(), signature.end(), header.begin()))) return Fail("header");
        if (!(r.Get<std::uint32_t>() == 0x14020007 && r.Get<std::uint8_t>() == 1 && r.Get<std::uint32_t>() == 12)) return Fail("version");
        const auto count = r.Count(65535, 6);
        if (!(count > 0 && r.Get<std::uint32_t>() == 100)) return Fail("stream-version");
        for (unsigned i = 0; i < 3; ++i)
          r.Take(r.Get<std::uint8_t>());
        const auto typeCount = r.Get<std::uint16_t>();
        if (!(typeCount > 0 && typeCount <= Names.size())) return Fail("type-count");

        std::vector<Kind> types;
        for (unsigned i = 0; i < typeCount; ++i)
        {
          const auto name  = r.Text(r.Get<std::uint32_t>());
          const auto found = std::ranges::find(Names, name);
          if (!(found != Names.end())) return Fail("unsupported-class:" + name);
          types.push_back(static_cast<Kind>(found - Names.begin()));
        }

        std::vector<std::uint32_t> sizes;
        for (std::uint32_t i = 0; i < count; ++i)
        {
          const auto type = r.Get<std::uint16_t>();
          if (!(type < types.size())) return Fail("type-index");
          blocks.push_back(Block{types[type]});
        }

        std::uint64_t total = 0;
        for (std::uint32_t i = 0; i < count; ++i)
        {
          const auto size = r.Get<std::uint32_t>();
          if (!(size > 0 && size <= limits.assetBytes)) return Fail("block-size");
          sizes.push_back(size);
          total += size;
        }
        if (!(total <= limits.assetBytes)) return Fail("block-total");

        strings              = r.Count(65535, 4);
        const auto maxString = r.Get<std::uint32_t>();
        if (!(maxString <= 4096)) return Fail("max-string");
        for (std::uint32_t i = 0; i < strings; ++i)
        {
          const auto length = r.Get<std::uint32_t>();
          if (!(length <= maxString)) return Fail("string-table");
          r.Text(length);
        }
        if (!(r.Get<std::uint32_t>() == 0)) return Fail("groups");

        for (std::size_t i = 0; i < blocks.size(); ++i)
          blocks[i].bytes = r.Take(sizes[i]);
        if (!(r.Get<std::uint32_t>() == 1)) return Fail("roots");
        const auto root = Ref(r, Kind::Node);
        if (!root) return std::unexpected(root.error());
        if (auto end = r.End(); !end) return std::unexpected(end.error());

        for (auto& block : blocks)
          if (auto checked = ReadBlock(block); !checked) return std::unexpected(checked.error());

        return BuildLayout(*root, count);
      }
    };

  }

  Result<Layout> Inspect(std::span<const std::uint8_t> bytes, const Limits& limits)
  {
    return Parser(limits).Read(bytes);
  }

}
