export module Dreamsleeve.Client.Phantom.Types;

import std;

export namespace Dreamsleeve::Client::Phantom
{

  template <class Tag, class T>
  struct Id
  {
    T    value{};
    auto operator<=>(const Id&) const = default;
  };
  using Generation                     = Id<struct GenerationTag, std::uint64_t>;
  using Sequence                       = Id<struct SequenceTag, std::uint64_t>;
  using TransferId                     = Id<struct TransferTag, std::uint64_t>;
  using RequestId                      = Id<struct RequestTag, std::uint64_t>;
  using NodeId                         = Id<struct NodeTag, std::uint32_t>;
  constexpr std::uint32_t NoNode       = std::numeric_limits<std::uint32_t>::max();
  constexpr std::uint32_t AssetVersion = 1;
  // Leave room for signed receipt/sample offset arithmetic on both ends.
  constexpr std::uint64_t MaximumSampleTime = std::numeric_limits<std::int64_t>::max() / 2;

  struct Vec3
  {
    float x{}, y{}, z{};
    bool  operator==(const Vec3&) const = default;
  };

  struct Quaternion
  {
    float x{}, y{}, z{}, w{1};
    bool  operator==(const Quaternion&) const = default;
  };

  struct Transform
  {
    Vec3       position;
    Quaternion rotation;
    float      scale{1};
    bool       operator==(const Transform&) const = default;
  };

  struct Bound
  {
    Vec3  center;
    float radius{};
    bool  operator==(const Bound&) const = default;
  };

  struct Vertex
  {
    Vec3                         position, normal, tangent;
    float                        u{}, v{};
    std::array<std::uint8_t, 4>  color{255, 255, 255, 255};
    std::array<float, 4>         weights{};
    std::array<std::uint16_t, 4> bones{};
  };

  struct AlphaMask
  {
    std::uint32_t             width{}, height{};
    std::vector<std::uint8_t> pixels;
  };

  struct Bone
  {
    NodeId    node;
    Transform bind;
    Bound     bound;
  };

  struct Skin
  {
    NodeId            root;
    Transform         worldToSkin;
    std::vector<Bone> bones;
  };

  struct Node
  {
    NodeId    parent{NoNode};
    Transform local;
  };

  struct Geometry
  {
    NodeId                           node;
    std::vector<Vertex>              vertices;
    std::vector<std::uint16_t>       indices;
    std::optional<Skin>              skin;
    std::shared_ptr<const AlphaMask> mask;
    std::uint8_t                     alphaThreshold{};
    bool                             alphaBlend{}, doubleSided{}, dynamic{};
  };

  // Detached values, never engine classes, paths, pointers or shader programs.
  struct Asset
  {
    std::vector<Node>     nodes;
    std::vector<Geometry> geometry;
  };

  struct Limits
  {
    std::uint32_t nodes{4096}, geometry{512}, vertices{2000000}, bonesPerSkin{512};
    std::uint32_t maskDimension{4096}, maskBytes{16 * 1024 * 1024};
    std::uint32_t assetBytes{128 * 1024 * 1024}, compressedAssetBytes{64 * 1024 * 1024};
    std::uint32_t poseBytes{512 * 1024}, compressedPoseBytes{256 * 1024};
  };
  enum class Failure
  {
    InvalidFormat,
    LimitExceeded,
    InvalidLink,
    InvalidNumber,
    InvalidGeometry,
    InvalidSkin,
    InvalidMask,
    UnsupportedGeometry,
    MissingSource,
    Busy,
    Stale,
    Storage,
    HashMismatch,
    Disconnected
  };

  struct Error
  {
    Failure     reason;
    std::string field;
  };
  template <class T>
  using Result = std::expected<T, Error>;

  class ValidatedAsset final
  {
public:

    const Asset& Value() const noexcept
    {
      return *asset;
    }

    std::uint64_t MemoryBytes() const noexcept
    {
      return memory;
    }

    static Result<ValidatedAsset> Parse(Asset asset, const Limits& limits = {});

private:

    explicit ValidatedAsset(Asset value, std::uint64_t bytes) : asset(std::make_shared<const Asset>(std::move(value))), memory(bytes) {}

    std::shared_ptr<const Asset> asset;
    std::uint64_t                memory{};
  };

  struct Channel
  {
    Transform world;
    bool      hidden{};
  };

  struct Deformation
  {
    std::uint32_t     geometry{};
    std::vector<Vec3> positions, normals;
  };

  struct Snapshot
  {
    Generation               generation;
    Sequence                 sequence;
    std::uint64_t            context{}, sampledAtUs{};
    Vec3                     origin;
    std::vector<Channel>     channels;
    std::vector<Bound>       bounds;
    std::vector<Deformation> deformations;
  };

  constexpr std::size_t BufferedPoseCount = 8;

  // Wire bytes are quantized; they are not a decoded allocation size. Include
  // vector capacity/metadata as well as unquantized channel and bound layouts.
  constexpr std::uint64_t SnapshotWorkingBytes(const Limits& limits = {})
  {
    return 2ULL * limits.poseBytes + limits.nodes * sizeof(Channel) + limits.geometry * (sizeof(Bound) + 2 * sizeof(Deformation)) +
           sizeof(Snapshot);
  }

  // Shared by the codec and scene adapter: a pose is atomic, including every
  // deforming mesh. Native engine adapters may add engine-specific checks.
  Result<void> CheckSnapshot(const Snapshot& snapshot, const ValidatedAsset& asset);
  enum class Representation
  {
    Disabled,
    Loading,
    Ready,
    Unavailable
  };

  struct ViewSettings
  {
    bool          publish{true}, receive{true}, fallback{true}, hideInCombat{false};
    std::uint32_t maximum{4};
    float         distance{4096}, opacity{0.6f};
    Vec3          color{0.55f, 0.8f, 1};
    std::uint32_t sampleRate{20}, delayMs{100}, extrapolationMs{100}, timeoutMs{1000};
    std::uint64_t memoryBytes{512 * 1024 * 1024}, diskBytes{1024ULL * 1024 * 1024};
    std::uint32_t uploadBytesPerSecond{5 * 1024 * 1024}, downloadBytesPerSecond{5 * 1024 * 1024};
    bool          operator==(const ViewSettings&) const = default;
  };

}
