module;

#include "Prelude.hpp"

export module Dreamsleeve.UI.Nameplates;

import std;
import Dreamsleeve.Client.Domain;

// A small Scaleform HUD layer. Game objects are read only on the main thread;
// the HUD callback consumes copied screen coordinates and owns all GFx values.
namespace Nameplates
{

  struct Label
  {
    Domain::PlayerId id{};
    std::string      text;
    float            x{}, y{}, size{};
  };

  struct Exchange
  {
    std::mutex         mutex;
    std::vector<Label> labels;
  };

  Exchange& GetExchange()
  {
    static Exchange exchange;
    return exchange;
  }

  export using Frame = std::vector<Label>;

  bool ClearSight(const RE::NiPoint3& from, const RE::NiPoint3& to)
  {
    auto* tes    = RE::TES::GetSingleton();
    auto* player = RE::PlayerCharacter::GetSingleton();
    if (!tes || !player || !player->GetParentCell()) return false;
    RE::bhkPickData pick{};
    const auto      scale = RE::bhkWorld::GetWorldScale();
    pick.rayInput.from    = from * scale;
    pick.rayInput.to      = to * scale;
    RE::CFilter playerFilter{};
    player->GetCollisionFilterInfo(playerFilter);
    pick.rayInput.filterInfo.filter = (playerFilter.filter & 0xFFFF0000u) | static_cast<std::uint32_t>(RE::COL_LAYER::kLineOfSight);
    tes->Pick(pick);
    return !pick.pickFailed && !pick.rayOutput.HasHit();
  }

  export void Add(Frame& frame, Domain::PlayerId id, const std::string& text, RE::NiPoint3 position, float size, bool occlusion)
  {
    // A VR HUD is a world-space plane: a flat camera projection would drift
    // between the eyes. Do not install the flat renderer on that runtime.
    if (REL::Module::IsVR() || text.empty()) return;
    auto* camera = RE::Main::WorldRootCamera();
    if (!camera) return;
    float x{}, y{}, z{};
    if (
      !camera->WorldPtToScreenPt3(position, x, y, z, 1e-5f) || !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) || z <= 0 ||
      x < 0 || x > 1 || y < 0 || y > 1)
      return;
    if (occlusion && !ClearSight(camera->world.translate, position)) return;
    frame.push_back({id, text, x, 1.0f - y, size});
  }

  export void Publish(Frame frame)
  {
    auto&            exchange = GetExchange();
    std::scoped_lock lock{exchange.mutex};
    exchange.labels = std::move(frame);
  }

  struct Field
  {
    RE::GFxValue value;
    std::string  text;
    float        size{};
    double       width{}, height{};
  };

  struct Renderer
  {
    // Declare movie first: the managed GFx values must die before the movie.
    RE::GPtr<RE::GFxMovieView>                  movie;
    RE::GFxValue                                layer;
    std::unordered_map<Domain::PlayerId, Field> fields;

    bool Bind(RE::GFxMovieView* current)
    {
      if (movie.get() != current)
      {
        fields.clear();
        if (layer.IsDisplayObject()) layer.Invoke("removeMovieClip", nullptr);
        layer.SetUndefined();
        movie.reset(current);
      }
      if (!movie) return false;
      if (layer.IsDisplayObject()) return true;
      RE::GFxValue root;
      if (!movie->GetVariable(&root, "_root") || !root.IsDisplayObject()) return false;
      // Let Scaleform choose a free depth; do not overwrite another HUD mod.
      if (!root.CreateEmptyMovieClip(&layer, "DreamsleeveNames")) return false;
      layer.SetMember("tabEnabled", RE::GFxValue(false));
      return layer.IsDisplayObject();
    }

    bool Create(Field& field, Domain::PlayerId id, float size)
    {
      auto         name = std::format("player_{}", id);
      RE::GFxValue depth;
      if (!layer.Invoke("getNextHighestDepth", &depth) || !depth.IsNumber()) return false;
      RE::GFxValue args[]{
          RE::GFxValue(name.c_str()),
          depth,
          RE::GFxValue(0.0),
          RE::GFxValue(0.0),
          RE::GFxValue(400.0),
          RE::GFxValue(static_cast<double>(size + 8))
      };
      if (
        !layer.Invoke("createTextField", nullptr, args, 6) || !layer.GetMember(name.c_str(), &field.value) ||
        !field.value.IsDisplayObject())
        return false;
      field.value.SetMember("selectable", RE::GFxValue(false));
      field.value.SetMember("multiline", RE::GFxValue(false));
      field.value.SetMember("wordWrap", RE::GFxValue(false));
      field.value.SetMember("autoSize", RE::GFxValue("center"));
      field.value.SetMember("embedFonts", RE::GFxValue(true));
      field.value.SetMember("background", RE::GFxValue(true));
      field.value.SetMember("backgroundColor", RE::GFxValue(0x151719));
      RE::GFxValue format;
      movie->CreateObject(&format, "TextFormat");
      format.SetMember("font", RE::GFxValue("$EverywhereFont"));
      format.SetMember("size", RE::GFxValue(static_cast<double>(size)));
      format.SetMember("color", RE::GFxValue(0xEEECE5));
      format.SetMember("align", RE::GFxValue("center"));
      field.value.Invoke("setNewTextFormat", nullptr, &format, 1);
      field.size = size;
      return true;
    }

    void Draw(RE::GFxMovieView* current, const Frame& frame)
    {
      if (!Bind(current)) return;
      std::unordered_set<Domain::PlayerId> visible;
      visible.reserve(frame.size());
      for (const auto& label : frame)
        visible.insert(label.id);
      std::erase_if(fields, [&](auto& entry) {
        if (visible.contains(entry.first)) return false;
        entry.second.value.Invoke("removeTextField", nullptr);
        return true;
      });
      const auto rect = movie->GetVisibleFrameRect();
      if (!(rect.right > rect.left && rect.bottom > rect.top)) return;
      for (const auto& label : frame)
      {
        auto& field = fields[label.id];
        if (field.value.IsDisplayObject() && field.size != label.size)
        {
          field.value.Invoke("removeTextField", nullptr);
          field = {};
        }
        if (!field.value.IsDisplayObject() && !Create(field, label.id, label.size))
        {
          fields.erase(label.id);
          continue;
        }
        if (field.text != label.text)
        {
          // Plain UTF-8 text, never HTML or ActionScript from the network.
          field.value.SetText(label.text.c_str());
          field.text = label.text;
          RE::GFxValue width, height;
          field.value.GetMember("_width", &width);
          field.value.GetMember("_height", &height);
          field.width  = width.IsNumber() ? width.GetNumber() : 0;
          field.height = height.IsNumber() ? height.GetNumber() : 0;
        }
        RE::GFxValue::DisplayInfo info;
        info.SetPosition(
          rect.left + label.x * (rect.right - rect.left) - field.width / 2,
          rect.top + label.y * (rect.bottom - rect.top) - field.height);
        info.SetVisible(true);
        field.value.SetDisplayInfo(info);
      }
    }
  };

  void                               Advance(RE::HUDMenu* menu, float interval, std::uint32_t time);
  REL::Relocation<decltype(Advance)> original;

  void Advance(RE::HUDMenu* menu, float interval, std::uint32_t time)
  {
    original(menu, interval, time);
    Frame frame;
    {
      auto&            exchange = GetExchange();
      std::scoped_lock lock{exchange.mutex};
      frame = exchange.labels;
    }
    static Renderer renderer;
    renderer.Draw(menu->uiMovie.get(), frame);
  }

  export void Install()
  {
    if (REL::Module::IsVR())
    {
      logger::info("Firefly names: flat HUD renderer disabled in VR (stereo renderer pending)");
      return;
    }
    // IMenu/HUDMenu::AdvanceMovie, slot 05 on SE and AE. No VR vtable patch.
    REL::Relocation<std::uintptr_t> vtable{RE::HUDMenu::VTABLE[0]};
    original = vtable.write_vfunc(0x05, Advance);
    logger::info("Scaleform firefly names installed");
  }

}
