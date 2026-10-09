#pragma once

// Synthetic native stream fixture; no game or third-party asset is embedded.
namespace PhantomFixture
{
  namespace P = Dreamsleeve::Client::Phantom;

  struct Writer
  {
    std::vector<std::uint8_t> data;

    template <class T>
    void Put(T v)
    {
      auto b = std::bit_cast<std::array<std::uint8_t, sizeof(T)>>(v);
      data.insert(data.end(), b.begin(), b.end());
    }

    void Text(std::string_view s)
    {
      data.insert(data.end(), s.begin(), s.end());
    }

    void Floats(unsigned n)
    {
      for (unsigned i = 0; i < n; ++i)
        Put(0.0f);
    }

    void Net()
    {
      Put(P::NoNode);
      Put(0u);
      Put(P::NoNode);
    }

    void AV()
    {
      Net();
      Put(0u);
      Floats(3);
      for (unsigned i = 0; i < 9; ++i)
        Put(i % 4 == 0 ? 1.0f : 0.0f);
      Put(1.0f);
      Put(P::NoNode);
    }
  };

  inline P::Asset Model(std::size_t channels = 2, std::uint16_t vertices = 3)
  {
    channels = std::max<std::size_t>(2, channels);
    std::vector<Writer> blocks(channels + 2);

    auto&               root = blocks[0];
    root.AV();
    root.Put(std::uint32_t(channels - 1));
    for (std::uint32_t i = 1; i < channels; ++i)
      root.Put(i);
    root.Put(0u);

    std::uint32_t random = 0x81724631;
    for (std::size_t i = 1; i < channels; ++i)
    {
      auto& s = blocks[i];
      s.AV();
      s.Floats(3);
      s.Put(1.0f);
      s.Put(P::NoNode);
      s.Put(std::uint32_t(channels));
      s.Put(P::NoNode);
      s.Put(std::uint64_t(4 | (1ULL << 44) | (1ULL << 54)));
      s.Put(std::uint16_t(1));
      s.Put(vertices);
      s.Put(std::uint32_t(vertices * 16 + 6));
      for (unsigned j = 0; j < vertices; ++j)
      {
        for (unsigned k = 0; k < 3; ++k)
        {
          random ^= random << 13;
          random ^= random >> 17;
          random ^= random << 5;
          s.Put(float(random & 0xffffff) / 65536.0f);
        }
        s.Put(0.0f);
      }
      s.Put(std::uint16_t(0));
      s.Put(std::uint16_t(1));
      s.Put(std::uint16_t(2));
      s.Put(0u);
    }

    auto& light = blocks[channels];
    light.Put(0u);
    light.Net();
    light.Put(0u);
    light.Put(0u);
    light.Floats(4);
    light.Put(std::uint32_t(channels + 1));
    light.Floats(4);
    light.Put(0u);
    light.Floats(9);
    blocks.back().Put(0u);

    Writer w;
    w.Text("Gamebryo File Format, Version 20.2.0.7\n");
    w.Put(0x14020007u);
    w.Put(std::uint8_t(1));
    w.Put(12u);
    w.Put(std::uint32_t(blocks.size()));
    w.Put(100u);
    for (unsigned i = 0; i < 3; ++i)
      w.Put(std::uint8_t(0));

    constexpr std::array<std::string_view, 4> types{"NiNode", "BSTriShape", "BSLightingShaderProperty", "BSShaderTextureSet"};
    w.Put(std::uint16_t(types.size()));
    for (auto t : types)
    {
      w.Put(std::uint32_t(t.size()));
      w.Text(t);
    }
    for (std::size_t i = 0; i < blocks.size(); ++i)
      w.Put(std::uint16_t(i == 0 ? 0 : i < channels ? 1 : i == channels ? 2 : 3));
    for (auto& b : blocks)
      w.Put(std::uint32_t(b.data.size()));
    w.Put(0u);
    w.Put(0u);
    w.Put(0u);

    for (auto& b : blocks)
      w.data.insert(w.data.end(), b.data.begin(), b.data.end());

    w.Put(1u);
    w.Put(0u);
    return {std::move(w.data)};
  }

}
