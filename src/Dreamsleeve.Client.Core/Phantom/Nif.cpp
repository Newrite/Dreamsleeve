import std;
import Dreamsleeve.Client.Phantom.Nif;

namespace Dreamsleeve::Client::Phantom::Nif
{
  namespace
  {

    struct Invalid
    {
      Error error;
    };

    void Require(bool condition, std::string_view field)
    {
      if (!condition)
        throw Invalid{
            {Failure::InvalidFormat, "nif." + std::string(field)}
        };
    }

    struct Reader
    {
      std::span<const std::uint8_t> bytes;
      std::size_t                   at{};

      std::span<const std::uint8_t> Take(std::size_t size)
      {
        Require(size <= bytes.size() - at, "truncated");
        const auto out  = bytes.subspan(at, size);
        at             += size;
        return out;
      }

      template <class T>
      T Get()
      {
        T          out;
        const auto data = Take(sizeof(T));
        std::memcpy(&out, data.data(), sizeof(T));
        return out;
      }

      std::uint32_t Count(std::uint32_t maximum, std::size_t stride)
      {
        const auto count = Get<std::uint32_t>();
        Require(count <= maximum && count <= (bytes.size() - at) / stride, "count");
        return count;
      }

      bool Bool()
      {
        const auto value = Get<std::uint8_t>();
        Require(value <= 1, "bool");
        return value != 0;
      }

      void Floats(std::size_t count)
      {
        for (std::size_t i = 0; i < count; ++i)
          Require(std::isfinite(Get<float>()), "number");
      }

      std::string Text(std::uint32_t length)
      {
        Require(length <= 4096, "string-length");
        const auto data = Take(length);
        Require(std::ranges::find(data, 0) == data.end(), "string-nul");
        return {reinterpret_cast<const char*>(data.data()), data.size()};
      }

      void End()
      {
        Require(at == bytes.size(), "block-length");
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
      std::vector<std::uint32_t>    children, bones;
      std::uint32_t                 skin{NoNode}, data{NoNode}, partition{NoNode}, root{NoNode};
      std::uint32_t                 vertices{}, boneCount{}, partitions{}, maximumVertex{}, maximumBone{};
      bool                          weights{};
    };

    class Parser
    {
      const Limits&      limits;
      std::vector<Block> blocks;
      std::uint32_t      strings{};
      Layout             result;

      std::uint32_t Ref(Reader& r, auto accepts, bool optional = false)
      {
        const auto id = r.Get<std::uint32_t>();
        if (optional && id == NoNode) return id;
        Require(id < blocks.size() && accepts(blocks[id].kind), "reference");
        return id;
      }

      std::uint32_t Ref(Reader& r, Kind kind, bool optional = false)
      {
        return Ref(r, [kind](Kind value) { return value == kind; }, optional);
      }

      void Net(Reader& r)
      {
        const auto name = r.Get<std::uint32_t>();
        Require(name == NoNode || name < strings, "name");
        Require(r.Get<std::uint32_t>() == 0, "extra-data");
        Require(r.Get<std::uint32_t>() == NoNode, "controller");
      }

      void AV(Reader& r)
      {
        Net(r);
        r.Get<std::uint32_t>();  // native flags
        r.Floats(12);
        const auto scale = r.Get<float>();
        Require(std::isfinite(scale) && scale > 0 && scale <= 1024, "scale");
        Require(r.Get<std::uint32_t>() == NoNode, "collision");
      }

      void Bound(Reader& r)
      {
        r.Floats(3);
        const auto radius = r.Get<float>();
        Require(std::isfinite(radius) && radius >= 0 && radius <= 100000, "bound");
      }

      std::uint32_t Descriptor(std::uint64_t descriptor)
      {
        const auto stride = static_cast<std::uint32_t>(descriptor & 15) * 4;
        Require(stride > 0 && stride <= 60, "vertex-stride");
        // Attribute footprints must fit the native buffer. Packed bytes stay
        // native; there is no vertex conversion or renderer here.
        constexpr std::array<unsigned, 9> size{8, 4, 4, 4, 4, 4, 12, 4, 4};
        for (unsigned i = 1; i < size.size(); ++i)
          if (descriptor & (1ULL << (44 + i)))
          {
            const auto offset = (descriptor >> (4 * i + 2)) & 0x3c;
            Require(offset + size[i] <= stride, "vertex-attribute");
          }
        Require((descriptor >> 55) == 0, "vertex-flags");
        return stride;
      }

      void Indices(Reader& r, std::size_t count, std::uint32_t vertices)
      {
        for (std::size_t i = 0; i < count; ++i)
          Require(r.Get<std::uint16_t>() < vertices, "vertex-index");
      }

      void Geometry(Reader& r, Block& b)
      {
        AV(r);
        Bound(r);
        b.skin = Ref(r, Skin, true);
        Ref(r, Kind::Lighting);
        Ref(r, Kind::Alpha, true);
        const auto descriptor = r.Get<std::uint64_t>();
        const auto stride     = Descriptor(descriptor);
        const auto triangles  = r.Get<std::uint16_t>();
        b.vertices            = r.Get<std::uint16_t>();
        Require((b.vertices > 0 && triangles > 0) || b.skin != NoNode, "empty-shape");
        const auto size = r.Get<std::uint32_t>();
        if (size)
        {
          Require(size == std::uint64_t(b.vertices) * stride + std::uint64_t(triangles) * 6, "shape-buffer-length");
          r.Take(std::size_t(b.vertices) * stride);
          Indices(r, std::size_t(triangles) * 3, b.vertices);
          result.vertexBytes += size;
        }
        else
          Require(b.skin != NoNode, "missing-shape-buffer");
        Require(r.Get<std::uint32_t>() == 0, "particle-data");
        if (b.kind == Kind::Dynamic)
        {
          Require(r.Get<std::uint32_t>() == b.vertices * 16, "dynamic-size");
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
            Require(start <= triangles * 3u && count <= triangles && start + std::uint64_t(count) * 3 <= triangles * 3u, "segment");
          }
        }
      }

      void Partition(Reader& r, Block& b)
      {
        b.partitions = r.Count(4096, 12);
        Require(b.partitions > 0, "partitions");
        const auto size = r.Get<std::uint32_t>(), stride = r.Get<std::uint32_t>();
        const auto descriptor = r.Get<std::uint64_t>();
        Require(stride == Descriptor(descriptor) && size && size % stride == 0, "partition-buffer");
        b.vertices = size / stride;
        Require(b.vertices <= 65535, "partition-vertices");
        r.Take(size);
        result.vertexBytes += size;
        for (std::uint32_t p = 0; p < b.partitions; ++p)
        {
          const auto vertices = r.Get<std::uint16_t>(), triangles = r.Get<std::uint16_t>(), bones = r.Get<std::uint16_t>();
          Require(vertices <= b.vertices && bones && bones <= 256, "partition-counts");
          Require(r.Get<std::uint16_t>() == 0, "strips");
          const auto weights = r.Get<std::uint16_t>();
          Require(weights == 4, "partition-weights");
          for (unsigned i = 0; i < bones; ++i)
            b.maximumBone = std::max(b.maximumBone, std::uint32_t(r.Get<std::uint16_t>()));
          if (r.Bool()) Indices(r, vertices, b.vertices);
          if (r.Bool())
            for (std::size_t i = 0; i < std::size_t(vertices) * weights; ++i)
            {
              const auto weight = r.Get<float>();
              Require(std::isfinite(weight) && weight >= 0 && weight <= 1, "weight");
            }
          if (r.Bool()) Indices(r, std::size_t(triangles) * 3, b.vertices);
          if (r.Bool())
            for (std::size_t i = 0; i < std::size_t(vertices) * weights; ++i)
              Require(r.Get<std::uint8_t>() < bones, "palette-index");
          r.Get<std::uint8_t>();
          r.Bool();
          Require(Descriptor(r.Get<std::uint64_t>()) == stride, "partition-descriptor");
          Indices(r, std::size_t(triangles) * 3, b.vertices);
        }
      }

      void ReadBlock(Block& b)
      {
        Reader r{b.bytes};
        if (Shape(b.kind))
          Geometry(r, b);
        else
          switch (b.kind)
          {
            case Kind::Node: {
              AV(r);
              const auto children = r.Count(limits.nodes, 4);
              for (std::uint32_t i = 0; i < children; ++i)
              {
                const auto child = Ref(r, Scene, true);
                if (child != NoNode) b.children.push_back(child);
              }
              Require(r.Get<std::uint32_t>() == 0, "effects");
              break;
            }
            case Kind::Lighting: {
              const auto type = r.Get<std::uint32_t>();
              Require(type <= 19, "lighting-type");
              Net(r);
              r.Get<std::uint32_t>();
              r.Get<std::uint32_t>();
              r.Floats(4);
              Ref(r, Kind::Textures);
              r.Floats(4);
              Require(r.Get<std::uint32_t>() <= 3, "texture-clamp");
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
              Net(r);
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
                Require(path.empty(), "external-texture");
              }
              break;
            }
            case Kind::Skin:
            case Kind::Dismember: {
              b.data      = Ref(r, Kind::SkinData);
              b.partition = Ref(r, Kind::Partition);
              b.root      = Ref(r, Kind::Node);
              b.boneCount = r.Count(limits.nodes, 4);
              Require(b.boneCount > 0, "skin-bones");
              for (std::uint32_t i = 0; i < b.boneCount; ++i)
                b.bones.push_back(Ref(r, Kind::Node));
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
                Bound(r);
                const auto vertices = r.Get<std::uint16_t>();
                if (b.weights)
                  for (std::uint32_t v = 0; v < vertices; ++v)
                  {
                    b.maximumVertex   = std::max(b.maximumVertex, std::uint32_t(r.Get<std::uint16_t>()));
                    const auto weight = r.Get<float>();
                    Require(std::isfinite(weight) && weight >= 0 && weight <= 1, "skin-weight");
                  }
              }
              break;
            }
            case Kind::Partition:
              Partition(r, b);
              break;
            default:
              Require(false, "class");
          }
        r.End();
      }

  public:

      explicit Parser(const Limits& value) : limits(value) {}

      Layout Read(std::span<const std::uint8_t> bytes)
      {
        Require(!bytes.empty() && bytes.size() <= limits.assetBytes, "size");
        Reader                     r{bytes};
        constexpr std::string_view header    = "Gamebryo File Format, Version 20.2.0.7\n";
        const auto                 signature = r.Take(header.size());
        Require(std::equal(signature.begin(), signature.end(), header.begin()), "header");
        Require(r.Get<std::uint32_t>() == 0x14020007 && r.Get<std::uint8_t>() == 1 && r.Get<std::uint32_t>() == 12, "version");
        const auto count = r.Count(65535, 6);
        Require(count > 0 && r.Get<std::uint32_t>() == 100, "stream-version");
        for (unsigned i = 0; i < 3; ++i)
          r.Take(r.Get<std::uint8_t>());
        const auto typeCount = r.Get<std::uint16_t>();
        Require(typeCount > 0 && typeCount <= Names.size(), "type-count");
        std::vector<Kind> types;
        for (unsigned i = 0; i < typeCount; ++i)
        {
          const auto name  = r.Text(r.Get<std::uint32_t>());
          const auto found = std::ranges::find(Names, name);
          Require(found != Names.end(), "unsupported-class:" + name);
          types.push_back(static_cast<Kind>(found - Names.begin()));
        }
        std::vector<std::uint32_t> sizes;
        for (std::uint32_t i = 0; i < count; ++i)
        {
          const auto type = r.Get<std::uint16_t>();
          Require(type < types.size(), "type-index");
          blocks.push_back(Block{types[type]});
        }
        std::uint64_t total = 0;
        for (std::uint32_t i = 0; i < count; ++i)
        {
          const auto size = r.Get<std::uint32_t>();
          Require(size > 0 && size <= limits.assetBytes, "block-size");
          sizes.push_back(size);
          total += size;
        }
        Require(total <= limits.assetBytes, "block-total");
        strings              = r.Count(65535, 4);
        const auto maxString = r.Get<std::uint32_t>();
        Require(maxString <= 4096, "max-string");
        for (std::uint32_t i = 0; i < strings; ++i)
        {
          const auto length = r.Get<std::uint32_t>();
          Require(length <= maxString, "string-table");
          r.Text(length);
        }
        Require(r.Get<std::uint32_t>() == 0, "groups");
        for (std::size_t i = 0; i < blocks.size(); ++i)
          blocks[i].bytes = r.Take(sizes[i]);
        Require(r.Get<std::uint32_t>() == 1, "roots");
        const auto root = Ref(r, Kind::Node);
        r.End();
        for (auto& block : blocks)
          ReadBlock(block);
        std::vector<std::uint32_t> ordinal(blocks.size(), NoNode);
        const auto                 visit = [&](auto&& self, std::uint32_t id, std::uint32_t parent, unsigned depth) -> void {
          Require(depth <= 256 && result.nodes.size() < limits.nodes && ordinal[id] == NoNode, "tree");
          const auto index = static_cast<std::uint32_t>(result.nodes.size());
          ordinal[id]      = index;
          result.nodes.push_back({id, parent, Shape(blocks[id].kind)});
          if (Shape(blocks[id].kind)) result.bounds.push_back(index);
          for (const auto child : blocks[id].children)
            self(self, child, index, depth + 1);
        };
        visit(visit, root, NoNode, 0);
        Require(!result.bounds.empty(), "no-geometry");
        std::set<std::uint32_t> channels{0};
        for (std::uint32_t i = 0; i < blocks.size(); ++i)
        {
          const auto& b = blocks[i];
          if (Scene(b.kind)) Require(ordinal[i] != NoNode, "unreachable-scene");
          if (Shape(b.kind))
          {
            channels.insert(ordinal[i]);
            if (b.skin != NoNode)
            {
              const auto& skin      = blocks[b.skin];
              const auto& data      = blocks[skin.data];
              const auto& partition = blocks[skin.partition];
              Require(skin.boneCount == data.boneCount && partition.maximumBone < skin.boneCount, "skin-counts");
              Require(
                (b.vertices == 0 || partition.vertices == b.vertices) && (!data.weights || data.maximumVertex < partition.vertices),
                "skin-vertices");
              if (skin.kind == Kind::Dismember) Require(skin.partitions == partition.partitions, "dismember-count");
              Require(ordinal[skin.root] != NoNode, "skin-root");
              channels.insert(ordinal[skin.root]);
              for (const auto bone : skin.bones)
              {
                Require(ordinal[bone] != NoNode, "skin-bone");
                channels.insert(ordinal[bone]);
              }
            }
          }
        }
        result.blocks = count;
        result.requiredChannels.assign(channels.begin(), channels.end());
        return std::move(result);
      }
    };

  }

  Result<Layout> Inspect(std::span<const std::uint8_t> bytes, const Limits& limits)
  {
    try
    {
      return Parser(limits).Read(bytes);
    }
    catch (const Invalid& invalid)
    {
      return std::unexpected(invalid.error);
    }
  }

}
