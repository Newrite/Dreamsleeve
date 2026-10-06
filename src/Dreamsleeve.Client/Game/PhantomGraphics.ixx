module;

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "Prelude.hpp"
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

export module Dreamsleeve.Game.PhantomGraphics;

import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomAsset;
import Dreamsleeve.Game.PhantomCapture;
import Dreamsleeve.Game.PhantomScene;

namespace Dreamsleeve::Game::PhantomGraphics
{
  namespace P = Dreamsleeve::Client::Phantom;
  namespace A = Dreamsleeve::Game::PhantomAsset;
  namespace C = Dreamsleeve::Game::PhantomCapture;
  namespace S = Dreamsleeve::Game::PhantomScene;
  template <class T>
  using Com = Microsoft::WRL::ComPtr<T>;

  // Hooks resolves ONLY these trusted, local class names in the NiStream
  // factory registry (no streams are constructed or loaded here).
  // Registry SE 523904/RVA3012408, AE410484/RVA3272D80, VR RVA316AC08.
  export enum class FactoryKind
  {
    Node,
    Mesh,
    Texture,
    Lighting,
    Alpha
  };
  export using Factory = RE::NiObject * (*)();

  export struct Ops
  {
    bool    (*mainThread)() noexcept {};
    Factory (*factory)(FactoryKind){};
    // Native SetMaterial: SE98897/RVA1291D40, AE105544/RVA147BFF0,
    // VR RVA12CA650. It copies/adopts through the material manager; the
    // input temporary remains caller-owned. true gives a unique material.
    void (*setMaterial)(RE::BSShaderProperty*, RE::BSShaderMaterial*, bool){};
    bool (*reserveReadbacks)(std::uint64_t totalPlannedBytes){};
  };

  namespace Detail
  {

    Ops                      ops;
    std::array<Factory, 5>   factories;
    Com<ID3D11ComputeShader> alphaShader;

    struct BufferRead
    {
      Com<ID3D11Buffer>      source, staging;
      std::uint32_t          bytes{};
      std::vector<std::byte> result;
      std::uint64_t          issuedMs{}, touchedMs{}, plannedBytes{};
    };

    struct MaskRead
    {
      RE::NiPointer<RE::NiSourceTexture> source;
      Com<ID3D11ShaderResourceView>      view;
      Com<ID3D11Texture2D>               staging;
      std::shared_ptr<P::AlphaMask>      result{std::make_shared<P::AlphaMask>()};
      std::uint64_t                      issuedMs{}, completedMs{}, touchedMs{};
    };

    std::vector<BufferRead> buffers;
    std::vector<MaskRead>   masks;

    inline std::uint64_t ReadbackBytes()
    {
      std::uint64_t total = buffers.capacity() * sizeof(BufferRead) + masks.capacity() * sizeof(MaskRead);
      for (const auto& read : buffers)
        total += read.plannedBytes;
      for (const auto& read : masks)
        total +=
          read.result->pixels.empty() ? std::uint64_t(read.result->width) * read.result->height * 8 : read.result->pixels.size() * 2ULL;
      return total;
    }

    inline bool ChargeReadbacks(std::uint64_t bytes)
    {
      return ops.reserveReadbacks && ops.reserveReadbacks(bytes);
    }

    struct PublishReadbacks
    {
      ~PublishReadbacks()
      {
        if (ops.reserveReadbacks) ops.reserveReadbacks(ReadbackBytes());
      }
    };

    inline P::Result<void> Thread()
    {
      if (!ops.mainThread || !ops.mainThread()) return A::Fail(P::Failure::Busy, "graphics.main-thread/not-installed");
      return {};
    }

    struct RendererGuard
    {
      RE::BSGraphics::Renderer* renderer{};
      CRITICAL_SECTION*         lock{};
      ID3D11Device*             device{};
      ID3D11DeviceContext*      context{};

      RendererGuard()
      {
        renderer = RE::BSGraphics::Renderer::GetSingleton();
        if (!renderer) return;
        auto* section = reinterpret_cast<CRITICAL_SECTION*>(&renderer->GetLock());
        if (!TryEnterCriticalSection(section)) return;
        lock          = section;
        auto& runtime = renderer->GetRuntimeData();
        device        = reinterpret_cast<ID3D11Device*>(runtime.forwarder);
        context       = reinterpret_cast<ID3D11DeviceContext*>(runtime.context);
      }

      ~RendererGuard()
      {
        if (lock) LeaveCriticalSection(lock);
      }

      explicit operator bool() const
      {
        return lock && device && context;
      }
    };

    // BSSpinLock uses the same two DWORDs in all three verified runtimes.
    // Try once, honoring recursive ownership; never spin on a physics job.
    struct DynamicGuard
    {
      std::uint32_t* words{};

      explicit DynamicGuard(RE::BSSpinLock& lock)
      {
        auto*           p      = reinterpret_cast<std::uint32_t*>(&lock);
        const auto      thread = GetCurrentThreadId();
        std::atomic_ref owner{p[0]}, count{p[1]};
        if (owner.load(std::memory_order_acquire) == thread)
        {
          count.fetch_add(1);
          words = p;
          return;
        }
        std::uint32_t empty = 0;
        if (!count.compare_exchange_strong(empty, 1, std::memory_order_acquire)) return;
        owner.store(thread, std::memory_order_release);
        words = p;
      }

      ~DynamicGuard()
      {
        if (!words) return;
        std::atomic_ref owner{words[0]}, count{words[1]};
        if (count.load() == 1)
        {
          owner.store(0, std::memory_order_release);
          count.store(0, std::memory_order_release);
        }
        else
          count.fetch_sub(1, std::memory_order_release);
      }
    };

    template <class T>
    P::Result<RE::NiPointer<T>> Make(FactoryKind kind, std::string_view name)
    {
      auto* object = factories[static_cast<std::size_t>(kind)]();
      if (!object) return A::Fail(P::Failure::Storage, "graphics.factory-allocation");
      RE::NiPointer<RE::NiObject> owner{object};
      if (!A::Kind(*object, name)) return A::Fail(P::Failure::UnsupportedGeometry, "graphics.factory-class");
      return RE::NiPointer<T>{static_cast<T*>(object)};
    }

    // GPU-only streams use an asynchronous staging copy. A retry observes the
    // existing copy, rather than reissuing it forever. Busy leaves the current
    // phantom frame in use. Cache admission precedes every GPU allocation.
    inline P::Result<std::vector<std::byte>> ReadBuffer(
      ID3D11Buffer&    source,
      std::uint32_t    bytes,
      RendererGuard&   renderer,
      const P::Limits& limits)
    {
      const auto       now = GetTickCount64();
      PublishReadbacks publish;
      // Open retries reuse completed streams while other resources are pending.
      // An audit explicitly invalidates its mesh once, then retries that copy.
      std::erase_if(buffers, [&](const auto& read) { return now - read.touchedMs >= 10000; });
      D3D11_BUFFER_DESC desc{};
      source.GetDesc(&desc);
      if (!bytes || bytes > desc.ByteWidth || desc.ByteWidth > limits.assetBytes)
        return A::Fail(P::Failure::LimitExceeded, "graphics.buffer-read-size");
      auto found = std::ranges::find_if(buffers, [&](const auto& read) { return read.source.Get() == &source && read.bytes == bytes; });
      if (found == buffers.end())
      {
        std::uint64_t total = std::uint64_t(desc.ByteWidth) * 2;
        for (const auto& read : buffers)
        {
          D3D11_BUFFER_DESC d{};
          read.source->GetDesc(&d);
          total += std::uint64_t(d.ByteWidth) * 2;
        }
        if (total > limits.assetBytes || buffers.size() >= std::uint64_t(limits.geometry) * 2)
          return A::Fail(P::Failure::LimitExceeded, "graphics.buffer-read-budget");
        desc.Usage               = D3D11_USAGE_STAGING;
        desc.BindFlags           = 0;
        desc.CPUAccessFlags      = D3D11_CPU_ACCESS_READ;
        desc.MiscFlags           = 0;
        desc.StructureByteStride = 0;
        BufferRead read;
        read.source   = &source;
        read.bytes    = bytes;
        read.issuedMs = read.touchedMs = now;
        read.plannedBytes              = std::uint64_t(desc.ByteWidth) * 2;
        const auto capacity            = std::max(buffers.capacity(), buffers.size() + 1);
        const auto planned             = ReadbackBytes() + read.plannedBytes + (capacity - buffers.capacity()) * sizeof(BufferRead);
        if (!ChargeReadbacks(planned)) return A::Fail(P::Failure::LimitExceeded, "graphics.client-readback-budget");
        buffers.reserve(capacity);
        if (FAILED(renderer.device->CreateBuffer(&desc, nullptr, &read.staging)))
          return A::Fail(P::Failure::Storage, "graphics.buffer-staging");
        renderer.context->CopyResource(read.staging.Get(), &source);
        buffers.push_back(std::move(read));
        return A::Fail(P::Failure::Busy, "graphics.buffer-pending");
      }
      found->touchedMs = now;
      if (!ChargeReadbacks(ReadbackBytes())) return A::Fail(P::Failure::LimitExceeded, "graphics.client-readback-budget");
      if (!found->result.empty()) return found->result;
      D3D11_MAPPED_SUBRESOURCE mapped{};
      const auto               status = renderer.context->Map(found->staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
      if (status == DXGI_ERROR_WAS_STILL_DRAWING) return A::Fail(P::Failure::Busy, "graphics.buffer-pending");
      if (FAILED(status)) return A::Fail(P::Failure::Storage, "graphics.buffer-map");
      found->result.resize(bytes);
      std::memcpy(found->result.data(), mapped.pData, bytes);
      renderer.context->Unmap(found->staging.Get(), 0);
      found->staging.Reset();
      return found->result;
    }

    struct PackedVertex
    {
      float         x, y, z, bitangentX;
      std::uint16_t u, v;
      std::uint8_t  normal[4], tangent[4], color[4];
    };

    static_assert(sizeof(PackedVertex) == 32);
    inline constexpr std::uint64_t Descriptor =
      8ULL | (4ULL << 8) | (5ULL << 16) | (6ULL << 20) | (7ULL << 24) |
      (std::uint64_t(
         RE::BSGraphics::Vertex::VF_VERTEX | RE::BSGraphics::Vertex::VF_UV | RE::BSGraphics::Vertex::VF_NORMAL |
         RE::BSGraphics::Vertex::VF_TANGENT | RE::BSGraphics::Vertex::VF_COLORS | RE::BSGraphics::Vertex::VF_FULLPREC)
       << 44);

    inline std::uint16_t Half(float value)
    {
      const auto bits     = std::bit_cast<std::uint32_t>(value);
      const auto sign     = std::uint16_t((bits >> 16) & 0x8000);
      int        exponent = int((bits >> 23) & 255) - 127 + 15;
      auto       mantissa = bits & 0x7fffff;
      if (exponent <= 0)
      {
        if (exponent < -10) return sign;
        mantissa           |= 0x800000;
        const auto shift    = 14 - exponent;
        const auto rounded  = (mantissa + ((1U << (shift - 1)) - 1) + ((mantissa >> shift) & 1)) >> shift;
        return sign | std::uint16_t(rounded);
      }
      mantissa += 0xfff + ((mantissa >> 13) & 1);
      if (mantissa & 0x800000)
      {
        mantissa = 0;
        ++exponent;
      }
      return sign | std::uint16_t(exponent << 10) | std::uint16_t(mantissa >> 13);
    }

    inline std::uint8_t Byte(float value)
    {
      return std::uint8_t(std::lround(std::clamp(value * 0.5f + 0.5f, 0.f, 1.f) * 255));
    }

    inline P::Result<std::vector<PackedVertex>> Pack(std::span<const A::RenderVertex> vertices)
    {
      if (vertices.empty() || vertices.size() > 65535) return A::Fail(P::Failure::LimitExceeded, "graphics.vertex-count");
      std::vector<PackedVertex> out;
      out.reserve(vertices.size());
      for (const auto& vertex : vertices)
      {
        if (
          !A::Finite(vertex.position) || !A::Finite(vertex.normal) || !A::Finite(vertex.tangent) || !std::isfinite(vertex.u) ||
          !std::isfinite(vertex.v) || std::abs(vertex.u) > 65504 || std::abs(vertex.v) > 65504)
          return A::Fail(P::Failure::InvalidNumber, "graphics.packed-vertex");
        const auto n = A::Unit(vertex.normal), t = A::Unit(vertex.tangent), b = A::Unit(A::Cross(n, t));
        out.push_back({
            vertex.position.x,
            vertex.position.y,
            vertex.position.z,
            b.x,
            Half(vertex.u),
            Half(vertex.v),
            {Byte(n.x),       Byte(n.y),       Byte(n.z),       Byte(b.y)      },
            {Byte(t.x),       Byte(t.y),       Byte(t.z),       Byte(b.z)      },
            {vertex.color[0], vertex.color[1], vertex.color[2], vertex.color[3]}
        });
      }
      return out;
    }

    // TriShape/Texture are plain, shared-layout renderer wrappers, not Ni
    // objects. Verified destructor frees 0x30/0x28 with the game allocator,
    // releases their COM pointers, and frees the two mesh CPU shadows.
    // SE D6C320/D6EC70; AE E46810/E49990; VR DBE0D0/DC0AE0.
    inline P::Result<RE::NiPointer<RE::NiSourceTexture>> Texture(RendererGuard& renderer, const P::AlphaMask* mask, bool normal = false)
    {
      auto texture = Make<RE::NiSourceTexture>(FactoryKind::Texture, "NiSourceTexture");
      if (!texture) return std::unexpected(texture.error());
      const auto width = mask ? mask->width : 1U, height = mask ? mask->height : 1U;
      if (!width || !height || width > 4096 || height > 4096 || (mask && std::uint64_t(width) * height != mask->pixels.size()))
        return A::Fail(P::Failure::InvalidMask, "graphics.generated-mask");
      std::vector<std::uint32_t> pixels(std::size_t(width) * height, normal ? 0xffff8080U : 0xffffffffU);
      if (mask)
        for (std::size_t i = 0; i < pixels.size(); ++i)
          pixels[i] = 0x00ffffffU | std::uint32_t(mask->pixels[i]) << 24;
      D3D11_TEXTURE2D_DESC desc{};
      desc.Width            = width;
      desc.Height           = height;
      desc.MipLevels        = 1;
      desc.ArraySize        = 1;
      desc.Format           = DXGI_FORMAT_R8G8B8A8_UNORM;
      desc.SampleDesc.Count = 1;
      desc.Usage            = D3D11_USAGE_IMMUTABLE;
      desc.BindFlags        = D3D11_BIND_SHADER_RESOURCE;
      const D3D11_SUBRESOURCE_DATA  data{pixels.data(), width * 4, 0};
      Com<ID3D11Texture2D>          resource;
      Com<ID3D11ShaderResourceView> view;
      if (
        FAILED(renderer.device->CreateTexture2D(&desc, &data, &resource)) ||
        FAILED(renderer.device->CreateShaderResourceView(resource.Get(), nullptr, &view)))
        return A::Fail(P::Failure::Storage, "graphics.generated-texture");
      auto* wrapper = RE::malloc<RE::BSGraphics::Texture>(sizeof(RE::BSGraphics::Texture));
      if (!wrapper) return A::Fail(P::Failure::Storage, "graphics.texture-wrapper");
      std::memset(wrapper, 0, sizeof(*wrapper));
      wrapper->refCount = 1;
      wrapper->width    = std::uint16_t(width);
      wrapper->height   = std::uint16_t(height);
      wrapper->mips     = 1;
      wrapper->format   = std::uint8_t(desc.Format);
      if ((*texture)->rendererTexture)
      {
        RE::free(wrapper);
        return A::Fail(P::Failure::InvalidGeometry, "graphics.texture-factory-not-empty");
      }
      wrapper->texture            = reinterpret_cast<REX::W32::ID3D11Resource*>(resource.Detach());
      wrapper->resourceView       = reinterpret_cast<REX::W32::ID3D11ShaderResourceView*>(view.Detach());
      (*texture)->rendererTexture = wrapper;
      return texture;
    }

    // Save and restore the exact compute slots touched by alpha extraction.
    // This does not invalidate Skyrim's render-state cache or alter graphics
    // stages; source and output resources are unbound before readback.
    struct ComputeState
    {
      ID3D11DeviceContext&                                                   context;
      Com<ID3D11ComputeShader>                                               shader;
      std::array<ID3D11ClassInstance*, 256>                                  instances{};
      UINT                                                                   count{256};
      Com<ID3D11ShaderResourceView>                                          view;
      std::array<ID3D11UnorderedAccessView*, D3D11_PS_CS_UAV_REGISTER_COUNT> outputs{};

      explicit ComputeState(ID3D11DeviceContext& c) : context(c)
      {
        context.CSGetShader(&shader, instances.data(), &count);
        context.CSGetShaderResources(0, 1, &view);
        context.CSGetUnorderedAccessViews(0, UINT(outputs.size()), outputs.data());
        std::array<ID3D11UnorderedAccessView*, D3D11_PS_CS_UAV_REGISTER_COUNT> empty{};
        std::array<UINT, D3D11_PS_CS_UAV_REGISTER_COUNT>                       preserve;
        preserve.fill(UINT_MAX);
        context.CSSetUnorderedAccessViews(0, UINT(empty.size()), empty.data(), preserve.data());
      }

      ~ComputeState()
      {
        ID3D11ShaderResourceView*  emptyView   = nullptr;
        ID3D11UnorderedAccessView* emptyOutput = nullptr;
        UINT                       preserve    = UINT_MAX;
        context.CSSetShaderResources(0, 1, &emptyView);
        context.CSSetUnorderedAccessViews(0, 1, &emptyOutput, &preserve);
        context.CSSetShader(shader.Get(), instances.data(), count);
        auto* v = view.Get();
        context.CSSetShaderResources(0, 1, &v);
        std::array<UINT, D3D11_PS_CS_UAV_REGISTER_COUNT> counters;
        counters.fill(UINT_MAX);
        context.CSSetUnorderedAccessViews(0, UINT(outputs.size()), outputs.data(), counters.data());
        for (auto* output : outputs)
          if (output) output->Release();
        for (UINT i = 0; i < count; ++i)
          if (instances[i]) instances[i]->Release();
      }
    };

    inline P::Result<void> CompileAlpha(RendererGuard& renderer)
    {
      if (alphaShader) return {};
      constexpr char source[] = R"(
        Texture2D<float4> diffuse : register(t0);
        RWTexture2D<uint> alpha : register(u0);
        [numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) {
          uint w,h; alpha.GetDimensions(w,h);
          if (id.x<w && id.y<h) alpha[id.xy]=(uint)round(saturate(diffuse.Load(int3(id.xy,0)).a)*255.0);
        })";
      Com<ID3DBlob>  code, error;
      if (
        FAILED(D3DCompile(
          source,
          sizeof(source) - 1,
          nullptr,
          nullptr,
          nullptr,
          "main",
          "cs_5_0",
          D3DCOMPILE_OPTIMIZATION_LEVEL3,
          0,
          &code,
          &error)) ||
        FAILED(renderer.device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &alphaShader)))
        return A::Fail(P::Failure::Storage, "graphics.alpha-shader");
      return {};
    }

  }

  // Install once after the renderer and native registry are ready. All five
  // factories are mandatory and checked BEFORE changing the installed table.
  export inline P::Result<void> Install(Ops ops)
  {
    if (!ops.mainThread || !ops.mainThread()) return A::Fail(P::Failure::Busy, "graphics.install-main-thread");
    if (!ops.factory || !ops.setMaterial || !ops.reserveReadbacks) return A::Fail(P::Failure::MissingSource, "graphics.install-ops");
    std::array<Factory, 5> resolved;
    for (std::size_t i = 0; i < resolved.size(); ++i)
    {
      resolved[i] = ops.factory(static_cast<FactoryKind>(i));
      if (!resolved[i]) return A::Fail(P::Failure::MissingSource, "graphics.registry-class:" + std::to_string(i));
    }
    if (!ops.reserveReadbacks(Detail::ReadbackBytes())) return A::Fail(P::Failure::LimitExceeded, "graphics.install-readback-budget");
    Detail::ops       = ops;
    Detail::factories = resolved;
    return {};
  }

  inline P::Result<void> InvalidateMasks()
  {
    auto thread = Detail::Thread();
    if (!thread) return thread;
    decltype(Detail::masks){}.swap(Detail::masks);
    Detail::ChargeReadbacks(Detail::ReadbackBytes());
    return {};
  }

  // Release all capture resource owners on cell/world/session reset or
  // third-person root replacement; keep installed factories and shader.
  export inline P::Result<void> ClearReadbacks()
  {
    auto thread = Detail::Thread();
    if (!thread) return thread;
    decltype(Detail::buffers){}.swap(Detail::buffers);
    decltype(Detail::masks){}.swap(Detail::masks);
    Detail::ChargeReadbacks(0);
    return {};
  }

  export inline P::Result<void> Shutdown()
  {
    auto thread = Detail::Thread();
    if (!thread) return thread;
    decltype(Detail::buffers){}.swap(Detail::buffers);
    decltype(Detail::masks){}.swap(Detail::masks);
    Detail::ChargeReadbacks(0);
    Detail::alphaShader.Reset();
    Detail::factories = {};
    Detail::ops       = {};
    return {};
  }

  inline P::Result<A::RawMesh> Buffer(
    RE::BSGraphics::TriShape& buffer,
    std::uint32_t             vertices,
    std::uint32_t             triangles,
    const P::Limits&          limits)
  {
    auto thread = Detail::Thread();
    if (!thread) return std::unexpected(thread.error());
    const auto descriptor = std::bit_cast<std::uint64_t>(buffer.vertexDesc);
    const auto stride     = std::uint32_t(descriptor & 15) * 4;
    if (
      !vertices || vertices > 65535 || vertices > limits.vertices || !triangles || triangles > 65535 || !stride || stride > 60 ||
      std::uint64_t(vertices) * stride > limits.assetBytes || std::uint64_t(triangles) * 6 > limits.assetBytes)
      return A::Fail(P::Failure::LimitExceeded, "graphics.mesh-read-shape");
    Detail::RendererGuard renderer;
    if (!renderer) return A::Fail(P::Failure::Busy, "graphics.renderer-lock");
    A::RawMesh out;
    out.descriptor   = descriptor;
    out.stride       = stride;
    out.vertexCount  = vertices;
    const auto bytes = vertices * stride, indexBytes = triangles * 6;
    if (buffer.rawVertexData)
    {
      out.vertices.resize(bytes);
      std::memcpy(out.vertices.data(), buffer.rawVertexData, bytes);
    }
    else
    {
      if (!buffer.vertexBuffer) return A::Fail(P::Failure::MissingSource, "graphics.vertex-buffer");
      auto copied = Detail::ReadBuffer(*reinterpret_cast<ID3D11Buffer*>(buffer.vertexBuffer), bytes, renderer, limits);
      if (!copied) return std::unexpected(copied.error());
      out.vertices = std::move(*copied);
    }
    if (buffer.rawIndexData)
      out.indices.assign(buffer.rawIndexData, buffer.rawIndexData + triangles * 3);
    else
    {
      if (!buffer.indexBuffer) return A::Fail(P::Failure::MissingSource, "graphics.index-buffer");
      auto copied = Detail::ReadBuffer(*reinterpret_cast<ID3D11Buffer*>(buffer.indexBuffer), indexBytes, renderer, limits);
      if (!copied) return std::unexpected(copied.error());
      out.indices.resize(triangles * 3);
      std::memcpy(out.indices.data(), copied->data(), indexBytes);
    }
    return out;
  }

  // Refresh once per audit transaction; Busy retries keep the same staging.
  inline P::Result<void> RefreshMesh(RE::BSTriShape& shape)
  {
    auto thread = Detail::Thread();
    if (!thread) return thread;
    Detail::PublishReadbacks publish;
    auto                     erase = [](RE::BSGraphics::TriShape* buffer) {
      if (!buffer) return;
      std::erase_if(Detail::buffers, [&](const auto& read) {
        return read.source.Get() == reinterpret_cast<ID3D11Buffer*>(buffer->vertexBuffer) ||
               read.source.Get() == reinterpret_cast<ID3D11Buffer*>(buffer->indexBuffer);
      });
    };
    auto& runtime = shape.GetGeometryRuntimeData();
    erase(runtime.rendererData);
    if (runtime.skinInstance && runtime.skinInstance->skinPartition)
    {
      auto& partitions = *runtime.skinInstance->skinPartition;
      if (partitions.numPartitions > P::Limits{}.geometry || (partitions.numPartitions && !partitions.partitions.data()))
        return A::Fail(P::Failure::LimitExceeded, "graphics.audit-partitions");
      for (std::uint32_t i = 0; i < partitions.numPartitions; ++i)
        erase(partitions.partitions.data()[i].buffData);
    }
    // Mesh-buffer audits must not invalidate shared texture appearance.
    // Source/SRV replacement selects another mask; explicit invalidation and
    // context teardown still clear resources when required.
    return {};
  }

  inline P::Result<std::vector<P::Vec3>> Dynamic(RE::BSDynamicTriShape& shape, std::uint32_t vertices, const P::Limits& limits)
  {
    auto thread = Detail::Thread();
    if (!thread) return std::unexpected(thread.error());
    if (!vertices || vertices > 65535 || vertices > limits.vertices || std::uint64_t(vertices) * 24 > limits.poseBytes)
      return A::Fail(P::Failure::LimitExceeded, "graphics.dynamic-shape");
    auto&                runtime = shape.GetDynamicTrishapeRuntimeData();
    Detail::DynamicGuard lock{runtime.lock};
    if (!lock.words) return A::Fail(P::Failure::Busy, "graphics.dynamic-lock");
    const auto descriptor = std::bit_cast<std::uint64_t>(shape.GetGeometryRuntimeData().vertexDesc);
    const auto stride     = std::uint32_t((descriptor >> 4) & 15) * 4;
    if (
      stride < 16 || stride > 60 || !runtime.dynamicData || runtime.dataSize < std::uint64_t(vertices) * stride ||
      runtime.dataSize > limits.assetBytes)
      return A::Fail(P::Failure::UnsupportedGeometry, "graphics.dynamic-layout");
    std::vector<P::Vec3> out;
    out.reserve(vertices);
    for (std::uint32_t i = 0; i < vertices; ++i)
    {
      P::Vec3 position;
      std::memcpy(&position, static_cast<const std::byte*>(runtime.dynamicData) + std::size_t(i) * stride, sizeof(position));
      if (!A::Finite(position)) return A::Fail(P::Failure::InvalidNumber, "graphics.dynamic-position");
      out.push_back(position);
    }
    return out;
  }

  inline P::Result<A::RawMesh> Mesh(RE::BSTriShape& shape, const P::Limits& limits)
  {
    auto thread = Detail::Thread();
    if (!thread) return std::unexpected(thread.error());
    Detail::RendererGuard renderer;
    if (!renderer) return A::Fail(P::Failure::Busy, "graphics.renderer-lock");
    return A::CopyCpuMesh(shape, limits, {Buffer, Dynamic});
  }

  inline P::Result<P::Deformation> Deformation(RE::BSTriShape& shape, std::uint32_t vertices, const P::Limits& limits)
  {
    auto* dynamic = shape.AsDynamicTriShape();
    if (!dynamic) return A::Fail(P::Failure::UnsupportedGeometry, "graphics.non-dynamic-deformation");
    auto positions = Dynamic(*dynamic, vertices, limits);
    if (!positions) return std::unexpected(positions.error());
    P::Deformation out;
    out.positions = std::move(*positions);
    return out;
  }

  inline P::Result<std::shared_ptr<const P::AlphaMask>> Mask(RE::NiSourceTexture& source, const P::Limits& limits)
  {
    auto thread = Detail::Thread();
    if (!thread) return std::unexpected(thread.error());
    Detail::RendererGuard renderer;
    if (!renderer) return A::Fail(P::Failure::Busy, "graphics.renderer-lock");
    auto* texture = source.rendererTexture;
    auto* view    = texture ? reinterpret_cast<ID3D11ShaderResourceView*>(texture->resourceView) : nullptr;
    if (!view) return A::Fail(P::Failure::MissingSource, "graphics.diffuse-view");
    D3D11_SHADER_RESOURCE_VIEW_DESC viewed{};
    view->GetDesc(&viewed);
    if (viewed.ViewDimension != D3D11_SRV_DIMENSION_TEXTURE2D)
      return A::Fail(P::Failure::UnsupportedGeometry, "graphics.alpha-array/cube/MSAA");
    switch (viewed.Format)
    {
      case DXGI_FORMAT_R8G8B8A8_UNORM:
      case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
      case DXGI_FORMAT_B8G8R8A8_UNORM:
      case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
      case DXGI_FORMAT_B8G8R8X8_UNORM:
      case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
      case DXGI_FORMAT_R16G16B16A16_FLOAT:
      case DXGI_FORMAT_R16G16B16A16_UNORM:
      case DXGI_FORMAT_BC1_UNORM:
      case DXGI_FORMAT_BC1_UNORM_SRGB:
      case DXGI_FORMAT_BC2_UNORM:
      case DXGI_FORMAT_BC2_UNORM_SRGB:
      case DXGI_FORMAT_BC3_UNORM:
      case DXGI_FORMAT_BC3_UNORM_SRGB:
      case DXGI_FORMAT_BC4_UNORM:
      case DXGI_FORMAT_BC5_UNORM:
      case DXGI_FORMAT_BC6H_UF16:
      case DXGI_FORMAT_BC6H_SF16:
      case DXGI_FORMAT_BC7_UNORM:
      case DXGI_FORMAT_BC7_UNORM_SRGB:
        break;
      default:
        return A::Fail(P::Failure::UnsupportedGeometry, "graphics.alpha-format");
    }
    Com<ID3D11Resource> resource;
    view->GetResource(&resource);
    Com<ID3D11Texture2D> native;
    if (FAILED(resource.As(&native))) return A::Fail(P::Failure::UnsupportedGeometry, "graphics.alpha-resource");
    D3D11_TEXTURE2D_DESC desc{};
    native->GetDesc(&desc);
    const auto mip = viewed.Texture2D.MostDetailedMip;
    if (mip >= desc.MipLevels || mip >= 32) return A::Fail(P::Failure::InvalidMask, "graphics.alpha-mip");
    const auto width = std::max(1U, desc.Width >> mip), height = std::max(1U, desc.Height >> mip);
    if (width > limits.maskDimension || height > limits.maskDimension || std::uint64_t(width) * height > limits.maskBytes)
      return A::Fail(P::Failure::LimitExceeded, "graphics.alpha-size");
    const auto               now = GetTickCount64();
    Detail::PublishReadbacks publish;
    std::erase_if(Detail::masks, [&](const auto& read) { return read.result.use_count() == 1 && now - read.touchedMs >= 10000; });
    auto found =
      std::ranges::find_if(Detail::masks, [&](const auto& read) { return read.source.get() == &source && read.view.Get() == view; });
    if (found != Detail::masks.end()) found->touchedMs = now;
    if (found != Detail::masks.end() && !found->result->pixels.empty())
    {
      if (!Detail::ChargeReadbacks(Detail::ReadbackBytes())) return A::Fail(P::Failure::LimitExceeded, "graphics.client-readback-budget");
      return found->result;
    }
    if (found == Detail::masks.end())
    {
      std::uint64_t total = std::uint64_t(width) * height * 8;
      for (const auto& read : Detail::masks)
        total += read.result->pixels.empty() ? std::uint64_t(read.result->width) * read.result->height * 8 : read.result->pixels.size();
      if (total > limits.assetBytes || Detail::masks.size() >= limits.geometry)
        return A::Fail(P::Failure::LimitExceeded, "graphics.alpha-read-budget");
      const auto capacity = std::max(Detail::masks.capacity(), Detail::masks.size() + 1);
      const auto planned =
        Detail::ReadbackBytes() + std::uint64_t(width) * height * 8 + (capacity - Detail::masks.capacity()) * sizeof(Detail::MaskRead);
      if (!Detail::ChargeReadbacks(planned)) return A::Fail(P::Failure::LimitExceeded, "graphics.client-readback-budget");
      Detail::masks.reserve(capacity);
      auto compiled = Detail::CompileAlpha(renderer);
      if (!compiled) return std::unexpected(compiled.error());
      D3D11_TEXTURE2D_DESC target{};
      target.Width            = width;
      target.Height           = height;
      target.MipLevels        = 1;
      target.ArraySize        = 1;
      target.Format           = DXGI_FORMAT_R32_UINT;
      target.SampleDesc.Count = 1;
      target.Usage            = D3D11_USAGE_DEFAULT;
      target.BindFlags        = D3D11_BIND_UNORDERED_ACCESS;
      Com<ID3D11Texture2D>           output;
      Com<ID3D11UnorderedAccessView> uav;
      if (
        FAILED(renderer.device->CreateTexture2D(&target, nullptr, &output)) ||
        FAILED(renderer.device->CreateUnorderedAccessView(output.Get(), nullptr, &uav)))
        return A::Fail(P::Failure::Storage, "graphics.alpha-output");
      target.Usage          = D3D11_USAGE_STAGING;
      target.BindFlags      = 0;
      target.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
      Detail::MaskRead read;
      read.issuedMs = read.touchedMs = now;
      read.source                    = RE::NiPointer<RE::NiSourceTexture>{&source};
      read.view                      = view;
      read.result->width             = width;
      read.result->height            = height;
      if (FAILED(renderer.device->CreateTexture2D(&target, nullptr, &read.staging)))
        return A::Fail(P::Failure::Storage, "graphics.alpha-staging");
      {
        Detail::ComputeState saved{*renderer.context};
        auto*                outputView = uav.Get();
        UINT                 preserve   = UINT_MAX;
        renderer.context->CSSetShader(Detail::alphaShader.Get(), nullptr, 0);
        renderer.context->CSSetShaderResources(0, 1, &view);
        renderer.context->CSSetUnorderedAccessViews(0, 1, &outputView, &preserve);
        renderer.context->Dispatch((width + 7) / 8, (height + 7) / 8, 1);
      }
      renderer.context->CopyResource(read.staging.Get(), output.Get());
      Detail::masks.push_back(std::move(read));
      return A::Fail(P::Failure::Busy, "graphics.alpha-pending");
    }
    if (!Detail::ChargeReadbacks(Detail::ReadbackBytes())) return A::Fail(P::Failure::LimitExceeded, "graphics.client-readback-budget");
    D3D11_MAPPED_SUBRESOURCE mapped{};
    const auto               status = renderer.context->Map(found->staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
    if (status == DXGI_ERROR_WAS_STILL_DRAWING) return A::Fail(P::Failure::Busy, "graphics.alpha-pending");
    if (FAILED(status)) return A::Fail(P::Failure::Storage, "graphics.alpha-map");
    if (mapped.RowPitch < width * 4)
    {
      renderer.context->Unmap(found->staging.Get(), 0);
      return A::Fail(P::Failure::InvalidMask, "graphics.alpha-row-pitch");
    }
    found->result->pixels.resize(std::size_t(width) * height);
    for (std::uint32_t y = 0; y < height; ++y)
      for (std::uint32_t x = 0; x < width; ++x)
      {
        std::uint32_t alpha;
        std::memcpy(&alpha, static_cast<const std::byte*>(mapped.pData) + std::size_t(y) * mapped.RowPitch + x * 4, 4);
        found->result->pixels[std::size_t(y) * width + x] = std::uint8_t(std::min(alpha, 255U));
      }
    renderer.context->Unmap(found->staging.Get(), 0);
    found->staging.Reset();
    found->completedMs = now;
    return found->result;
  }

  inline P::Result<RE::NiPointer<RE::NiNode>> MakeNode()
  {
    auto thread = Detail::Thread();
    if (!thread) return std::unexpected(thread.error());
    return Detail::Make<RE::NiNode>(FactoryKind::Node, "NiNode");
  }

  inline P::Result<void> Look(RE::BSTriShape& mesh, S::Look look)
  {
    auto thread = Detail::Thread();
    if (!thread) return thread;
    if (!S::Valid(look)) return A::Fail(P::Failure::InvalidNumber, "graphics.look");
    auto* property = mesh.lightingShaderProp_cast();
    if (
      !property || !property->material || !property->emissiveColor ||
      property->material->GetType() != RE::BSShaderMaterial::Type::kLighting)
      return A::Fail(P::Failure::InvalidGeometry, "graphics.look-material");
    property->SetMaterialAlpha(look.opacity);
    auto& alpha = mesh.GetGeometryRuntimeData().alphaProperty;
    if (!alpha) return A::Fail(P::Failure::InvalidGeometry, "graphics.look-alpha");
    // Skyrim tests diffuse*vertex*material alpha. Scale the native cutoff
    // with phantom opacity, otherwise opaque high-cutoff hair disappears.
    alpha->alphaThreshold         = std::uint8_t(std::floor(look.alphaThreshold * look.opacity));
    property->alpha               = 1;
    *property->emissiveColor      = {look.color.x, look.color.y, look.color.z};
    property->emissiveMult        = 1;
    property->lastRenderPassState = std::numeric_limits<std::int32_t>::max();
    property->DoClearRenderPasses();
    return {};
  }

  inline P::Result<void> Upload(RE::BSTriShape& mesh, std::span<const A::RenderVertex> vertices)
  {
    auto thread = Detail::Thread();
    if (!thread) return thread;
    auto packed = Detail::Pack(vertices);
    if (!packed) return std::unexpected(packed.error());
    auto* data = mesh.GetGeometryRuntimeData().rendererData;
    if (
      !data || !data->vertexBuffer || !data->rawVertexData || std::bit_cast<std::uint64_t>(data->vertexDesc) != Detail::Descriptor ||
      mesh.GetTrishapeRuntimeData().vertexCount != vertices.size())
      return A::Fail(P::Failure::InvalidGeometry, "graphics.upload-owner/layout");
    Detail::RendererGuard renderer;
    if (!renderer) return A::Fail(P::Failure::Busy, "graphics.renderer-lock");
    auto*             buffer = reinterpret_cast<ID3D11Buffer*>(data->vertexBuffer);
    D3D11_BUFFER_DESC desc{};
    buffer->GetDesc(&desc);
    if (desc.Usage != D3D11_USAGE_DYNAMIC || desc.ByteWidth != packed->size() * sizeof(Detail::PackedVertex))
      return A::Fail(P::Failure::InvalidGeometry, "graphics.upload-buffer");
    D3D11_MAPPED_SUBRESOURCE mapped{};
    const auto               status = renderer.context->Map(buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    if (FAILED(status)) return A::Fail(P::Failure::Storage, "graphics.upload-map");
    std::memcpy(mapped.pData, packed->data(), desc.ByteWidth);
    renderer.context->Unmap(buffer, 0);
    std::memcpy(data->rawVertexData, packed->data(), desc.ByteWidth);
    return {};
  }

  inline P::Result<RE::NiPointer<RE::BSTriShape>> MakeMesh(const S::MeshCreate& request)
  {
    auto thread = Detail::Thread();
    if (!thread) return std::unexpected(thread.error());
    if (
      !S::Valid(request.look) || request.indices.empty() || request.indices.size() % 3 || request.indices.size() > 65535 * 3 ||
      std::ranges::any_of(request.indices, [&](auto index) { return index >= request.vertices.size(); }))
      return A::Fail(P::Failure::InvalidGeometry, "graphics.mesh-create");
    auto packed = Detail::Pack(request.vertices);
    if (!packed) return std::unexpected(packed.error());
    Detail::RendererGuard renderer;
    if (!renderer) return A::Fail(P::Failure::Busy, "graphics.renderer-lock");
    auto mesh = Detail::Make<RE::BSTriShape>(FactoryKind::Mesh, "BSTriShape");
    if (!mesh) return std::unexpected(mesh.error());
    auto lighting = Detail::Make<RE::BSLightingShaderProperty>(FactoryKind::Lighting, "BSLightingShaderProperty");
    if (!lighting) return std::unexpected(lighting.error());
    auto alpha = Detail::Make<RE::NiAlphaProperty>(FactoryKind::Alpha, "NiAlphaProperty");
    if (!alpha) return std::unexpected(alpha.error());
    auto diffuse = Detail::Texture(renderer, request.mask);
    if (!diffuse) return std::unexpected(diffuse.error());
    auto normal = Detail::Texture(renderer, nullptr, true);
    if (!normal) return std::unexpected(normal.error());
    if (!(*lighting)->material) return A::Fail(P::Failure::Storage, "graphics.default-material-not-ready");
    std::unique_ptr<RE::BSShaderMaterial> temporary{(*lighting)->material->Create()};
    if (
      !temporary || temporary->GetType() != RE::BSShaderMaterial::Type::kLighting ||
      temporary->GetFeature() != RE::BSShaderMaterial::Feature::kDefault)
      return A::Fail(P::Failure::InvalidGeometry, "graphics.default-material-factory");
    auto& material              = *static_cast<RE::BSLightingShaderMaterialBase*>(temporary.get());
    material.diffuseTexture     = *diffuse;
    material.normalTexture      = *normal;
    material.materialAlpha      = request.look.opacity;
    material.specularColor      = {0, 0, 0};
    material.specularColorScale = 0;
    material.textureClampMode   = 3;
    material.textureSet.reset();
    Detail::ops.setMaterial(lighting->get(), temporary.get(), true);
    if (!(*lighting)->material || (*lighting)->material == temporary.get())
      return A::Fail(P::Failure::InvalidGeometry, "graphics.unique-material-copy");
    using Flag         = RE::BSShaderProperty::EShaderPropertyFlag;
    (*lighting)->flags = {Flag::kOwnEmit, Flag::kZBufferTest, Flag::kVertexColors, Flag::kVertexAlpha};
    (*lighting)->flags.set(request.doubleSided, Flag::kTwoSided);
    (*alpha)->alphaFlags     = 1 | (6 << 1) | (7 << 5) | (1 << 9) | (4 << 10);
    (*alpha)->alphaThreshold = request.alphaThreshold;
    // Blend state is always enabled for phantom opacity. Source alpha-test
    // cards retain their mask and threshold; depth tests remain enabled.
    Com<ID3D11Buffer> vertexBuffer, indexBuffer;
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth      = UINT(packed->size() * sizeof(Detail::PackedVertex));
    desc.BindFlags      = D3D11_BIND_VERTEX_BUFFER;
    desc.Usage          = request.mutableVertices ? D3D11_USAGE_DYNAMIC : D3D11_USAGE_IMMUTABLE;
    desc.CPUAccessFlags = request.mutableVertices ? D3D11_CPU_ACCESS_WRITE : 0;
    D3D11_SUBRESOURCE_DATA initial{packed->data(), 0, 0};
    if (FAILED(renderer.device->CreateBuffer(&desc, &initial, &vertexBuffer)))
      return A::Fail(P::Failure::Storage, "graphics.vertex-allocation");
    desc.ByteWidth      = UINT(request.indices.size() * 2);
    desc.BindFlags      = D3D11_BIND_INDEX_BUFFER;
    desc.Usage          = D3D11_USAGE_IMMUTABLE;
    desc.CPUAccessFlags = 0;
    initial.pSysMem     = request.indices.data();
    if (FAILED(renderer.device->CreateBuffer(&desc, &initial, &indexBuffer)))
      return A::Fail(P::Failure::Storage, "graphics.index-allocation");
    auto* wrapper = RE::malloc<RE::BSGraphics::TriShape>(sizeof(RE::BSGraphics::TriShape));
    if (!wrapper) return A::Fail(P::Failure::Storage, "graphics.mesh-wrapper");
    std::memset(wrapper, 0, sizeof(*wrapper));
    wrapper->rawVertexData = RE::malloc<std::uint8_t>(packed->size() * sizeof(Detail::PackedVertex));
    wrapper->rawIndexData  = RE::malloc<std::uint16_t>(request.indices.size() * 2);
    if (!wrapper->rawVertexData || !wrapper->rawIndexData)
    {
      RE::free(wrapper->rawVertexData);
      RE::free(wrapper->rawIndexData);
      RE::free(wrapper);
      return A::Fail(P::Failure::Storage, "graphics.mesh-shadow");
    }
    wrapper->refCount     = 1;
    wrapper->vertexDesc   = std::bit_cast<RE::BSGraphics::VertexDesc>(Detail::Descriptor);
    wrapper->vertexBuffer = reinterpret_cast<REX::W32::ID3D11Buffer*>(vertexBuffer.Detach());
    wrapper->indexBuffer  = reinterpret_cast<REX::W32::ID3D11Buffer*>(indexBuffer.Detach());
    std::memcpy(wrapper->rawVertexData, packed->data(), packed->size() * sizeof(Detail::PackedVertex));
    std::memcpy(wrapper->rawIndexData, request.indices.data(), request.indices.size() * 2);
    auto& runtime        = (*mesh)->GetGeometryRuntimeData();
    runtime.rendererData = wrapper;
    runtime.vertexDesc   = wrapper->vertexDesc;
    runtime.skinInstance.reset();
    runtime.shaderProperty      = *lighting;
    runtime.alphaProperty       = *alpha;
    auto& counts                = (*mesh)->GetTrishapeRuntimeData();
    counts.vertexCount          = std::uint16_t(request.vertices.size());
    counts.triangleCount        = std::uint16_t(request.indices.size() / 3);
    auto materialLook           = request.look;
    materialLook.alphaThreshold = request.alphaThreshold;
    auto changed                = Look(**mesh, materialLook);
    if (!changed) return std::unexpected(changed.error());
    if (!(*lighting)->SetupGeometry(mesh->get()) || !(*lighting)->FinishSetupGeometry(mesh->get()))
      return A::Fail(P::Failure::InvalidGeometry, "graphics.shader-setup");
    return mesh;
  }

  export inline C::Engine CaptureEngine()
  {
    return {Detail::ops.mainThread, Mesh, Mask, Deformation, RefreshMesh};
  }

  export inline S::Engine SceneEngine()
  {
    return {Detail::ops.mainThread, MakeNode, MakeMesh, Upload, Look};
  }

  inline A::Readback Readback()
  {
    return {Buffer, Dynamic};
  }

}
