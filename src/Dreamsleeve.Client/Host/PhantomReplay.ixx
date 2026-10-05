export module Dreamsleeve.Host.PhantomReplay;

import std;
import Dreamsleeve.Host.PhantomArchive;

// Local replay candidates. No scene objects or assumptions about network delivery.
export namespace PhantomReplay
{

  using Pose                   = PhantomArchive::Pose;
  constexpr float PositionStep = 1.0f / 16;

  std::vector<std::uint32_t> Channels(const PhantomArchive::Metadata& metadata)
  {
    std::set<std::uint32_t> selected{0};
    for (std::uint32_t i = 0; i < metadata.nodes.size(); ++i)
      if (metadata.nodes[i].geometry && !metadata.nodes[i].excluded) selected.insert(i);
    for (const auto& skin : metadata.skins)
      if (selected.contains(skin.geometry))
      {
        if (skin.root != PhantomArchive::NoParent) selected.insert(skin.root);
        selected.insert(skin.bones.begin(), skin.bones.end());
      }
    if (!std::ranges::all_of(selected, [&](auto i) { return i < metadata.nodes.size(); }))
      throw std::runtime_error("Invalid replay channel links");
    return {selected.begin(), selected.end()};
  }

  std::array<float, 4> Quaternion(const std::array<float, 17>& m)
  {
    std::array<float, 4> q{};  // x, y, z, w; same convention as the offline benchmark.
    const auto           trace = m[0] + m[4] + m[8];
    if (trace > 0)
    {
      const auto s = std::sqrt(trace + 1) * 2;
      q            = {(m[7] - m[5]) / s, (m[2] - m[6]) / s, (m[3] - m[1]) / s, s / 4};
    }
    else
    {
      const auto i = m[0] >= m[4] && m[0] >= m[8] ? 0 : m[4] >= m[8] ? 1 : 2;
      const auto j = (i + 1) % 3, k = (i + 2) % 3;
      const auto s = std::sqrt(std::max(0.0f, 1 + m[i * 3 + i] - m[j * 3 + j] - m[k * 3 + k])) * 2;
      if (s < 0.000001f) throw std::runtime_error("Degenerate replay rotation");
      q[i] = s / 4;
      q[j] = (m[j * 3 + i] + m[i * 3 + j]) / s;
      q[k] = (m[k * 3 + i] + m[i * 3 + k]) / s;
      q[3] = (m[k * 3 + j] - m[j * 3 + k]) / s;
    }
    auto length = 0.0f;
    for (auto x : q)
      length += x * x;
    length = std::sqrt(length);
    if (!std::isfinite(length) || length < 0.000001f) throw std::runtime_error("Invalid replay quaternion");
    const auto divisor = q[3] < 0 ? -length : length;
    for (auto& x : q)
      x /= divisor;
    return q;
  }

  void Rotation(Pose& pose, std::array<float, 4> q)
  {
    float length = 0;
    for (auto x : q)
      length += x * x;
    length = std::sqrt(length);
    if (!std::isfinite(length) || length < 0.000001f) throw std::runtime_error("Invalid decoded quaternion");
    for (auto& x : q)
      x /= length;
    const auto [x, y, z, w] = q;
    const std::array<float, 9> matrix{
        1 - 2 * (y * y + z * z),
        2 * (x * y - z * w),
        2 * (x * z + y * w),
        2 * (x * y + z * w),
        1 - 2 * (x * x + z * z),
        2 * (y * z - x * w),
        2 * (x * z - y * w),
        2 * (y * z + x * w),
        1 - 2 * (x * x + y * y)
    };
    std::ranges::copy(matrix, pose.values.begin());
  }

  struct Clip
  {
    std::vector<std::uint32_t>     channels;
    std::array<float, 3>           origin{};
    std::vector<std::vector<char>> frames;
    bool                           quantized{};

    std::uint64_t Bytes() const
    {
      std::uint64_t size = channels.size() * 4 + 12;
      for (const auto& frame : frames)
        size += frame.size();
      return size;
    }

    Pose Decode(std::size_t frame, std::size_t channel) const
    {
      const auto  stride = quantized ? 23 : 33;
      const auto& bytes  = frames.at(frame);
      if (channel >= channels.size() || bytes.size() != channels.size() * stride) throw std::runtime_error("Invalid compact replay frame");
      std::istringstream   in(std::string(bytes.data() + channel * stride, stride), std::ios::binary);
      Pose                 pose;
      std::array<float, 4> q;
      if (quantized)
      {
        for (std::size_t j = 0; j < 3; ++j)
          pose.values[9 + j] = origin[j] + PhantomArchive::ReadScalar<std::int32_t>(in) * PositionStep;
        for (auto& x : q)
          x = PhantomArchive::ReadScalar<std::int16_t>(in) / 32767.0f;
        pose.values[12] = PhantomArchive::ReadScalar<std::uint16_t>(in) / 1024.0f;
      }
      else
      {
        for (std::size_t j = 0; j < 3; ++j)
          pose.values[9 + j] = PhantomArchive::ReadScalar<float>(in);
        for (auto& x : q)
          x = PhantomArchive::ReadScalar<float>(in);
        pose.values[12] = PhantomArchive::ReadScalar<float>(in);
      }
      const auto hidden = PhantomArchive::ReadScalar<std::uint8_t>(in);
      if (hidden > 1) throw std::runtime_error("Invalid compact visibility");
      pose.hidden = hidden != 0;
      Rotation(pose, q);
      return pose;
    }
  };

  Clip Encode(const PhantomArchive::Metadata& metadata, const std::vector<PhantomArchive::Frame>& frames, bool quantized)
  {
    if (frames.empty() || frames.front().poses.empty()) throw std::runtime_error("Empty replay capture");
    Clip clip;
    clip.channels  = Channels(metadata);
    clip.quantized = quantized;
    for (std::size_t j = 0; j < 3; ++j)
      clip.origin[j] = frames.front().poses[0].values[9 + j];
    for (const auto& frame : frames)
    {
      if (frame.poses.size() != metadata.nodes.size()) throw std::runtime_error("Replay pose count mismatch");
      std::ostringstream out(std::ios::binary);
      for (auto i : clip.channels)
      {
        const auto& pose = frame.poses[i];
        if (!std::ranges::all_of(pose.values, [](auto x) { return std::isfinite(x); })) throw std::runtime_error("Non-finite replay pose");
        const auto q = Quaternion(pose.values);
        if (quantized)
        {
          for (std::size_t j = 0; j < 3; ++j)
          {
            const auto value = std::round((static_cast<double>(pose.values[9 + j]) - clip.origin[j]) / PositionStep);
            if (value < std::numeric_limits<std::int32_t>::min() || value > std::numeric_limits<std::int32_t>::max())
              throw std::runtime_error("Replay position range exceeded");
            PhantomArchive::Scalar(out, static_cast<std::int32_t>(value));
          }
          for (auto x : q)
            PhantomArchive::Scalar(out, static_cast<std::int16_t>(std::round(x * 32767)));
          const auto scale = std::round(static_cast<double>(pose.values[12]) * 1024);
          if (scale < 0 || scale > 65535) throw std::runtime_error("Replay scale range exceeded");
          PhantomArchive::Scalar(out, static_cast<std::uint16_t>(scale));
        }
        else
        {
          for (std::size_t j = 0; j < 3; ++j)
            PhantomArchive::Scalar(out, pose.values[9 + j]);
          for (auto x : q)
            PhantomArchive::Scalar(out, x);
          PhantomArchive::Scalar(out, pose.values[12]);
        }
        PhantomArchive::Scalar(out, static_cast<std::uint8_t>(pose.hidden));
      }
      const auto bytes = std::move(out).str();
      clip.frames.emplace_back(bytes.begin(), bytes.end());
    }
    return clip;
  }

  struct Prepared
  {
    PhantomArchive::Capture        capture;
    Clip                           selected, quantized;
    std::vector<std::vector<Pose>> selectedPoses, quantizedPoses;
  };

  Prepared Prepare(PhantomArchive::Capture capture)
  {
    Prepared result;
    result.capture   = std::move(capture);
    result.selected  = Encode(result.capture.metadata, result.capture.frames, false);
    result.quantized = Encode(result.capture.metadata, result.capture.frames, true);
    auto decode      = [](const Clip& clip) {
      std::vector<std::vector<Pose>> frames;
      for (std::size_t f = 0; f < clip.frames.size(); ++f)
      {
        std::vector<Pose> poses;
        poses.reserve(clip.channels.size());
        for (std::size_t i = 0; i < clip.channels.size(); ++i)
          poses.push_back(clip.Decode(f, i));
        frames.push_back(std::move(poses));
      }
      return frames;
    };
    result.selectedPoses  = decode(result.selected);
    result.quantizedPoses = decode(result.quantized);
    return result;
  }

}
