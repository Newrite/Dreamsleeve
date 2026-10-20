module;

#include "Prelude.hpp"

export module Dreamsleeve.UI.Nameplates;

import std;
import Dreamsleeve.Client.Domain.Logic;
import Dreamsleeve.Client.Utils;
import Dreamsleeve.Host.Hud;
import Dreamsleeve.Game.Raycast;

// A small Scaleform HUD layer: a name and, optionally, one text bubble above
// each anchor (a firefly, a ground mark). Game objects are read only on the
// main thread; the HUD callback consumes copied strings and screen coordinates
// and owns all GFx values.
namespace Nameplates
{

  using Dreamsleeve::Host::Hud::BubbleStyle;

  // What a label stands for; the GFx object names derive from it, so a player
  // and a mark with the same numeric ID never share a text field.
  export enum class LabelKind : std::uint8_t
  {
    Player,
    Note,
    Death
  };

  export struct LabelKey
  {
    LabelKind     kind{LabelKind::Player};
    std::uint64_t id{};

    bool operator==(const LabelKey&) const = default;
  };

  struct LabelKeyHash
  {
    std::size_t operator()(const LabelKey& key) const noexcept
    {
      return std::hash<std::uint64_t>{}(key.id) ^ (static_cast<std::size_t>(key.kind) << 60);
    }
  };

  // A phantom on its way, drawn as a small ring beside the name: the share of
  // the model received fills it; a stage with nothing to measure (waiting,
  // checking, building) spins an arc instead.
  export struct Loading
  {
    std::optional<float> share;

    bool operator==(const Loading&) const = default;
  };

  // All texts are plain UTF-8. An empty name hides the name field but keeps
  // its baseline, so the bubble never jumps when names are switched off. A
  // header is a smaller line on top inside the same box (a mark's date).
  export struct Label
  {
    LabelKey               key{};
    std::string            name;
    float                  nameSize{};
    std::uint32_t          nameColor{Dreamsleeve::Host::Hud::DefaultTextColor};
    std::string            header;
    std::string            bubble;
    float                  bubbleAlpha{1.0f};
    BubbleStyle            style;
    std::optional<Loading> loading;
    float                  x{}, y{};  // Filled by Add: normalized screen position of the anchor.
  };

  export struct Frame
  {
    std::vector<Label> labels;
  };

  struct Exchange
  {
    std::mutex mutex;
    Frame      frame;
  };

  Exchange& GetExchange()
  {
    static Exchange exchange;
    return exchange;
  }

  // One projection and at most one line-of-sight pick per label serve both
  // the name and the bubble.
  export void Add(Frame& frame, Label label, RE::NiPoint3 anchor, bool occlusion)
  {
    // A VR HUD is a world-space plane: a flat camera projection would drift
    // between the eyes. Do not install the flat renderer on that runtime.
    if (REL::Module::IsVR() || (label.name.empty() && label.bubble.empty() && label.header.empty() && !label.loading)) return;
    auto* camera = RE::Main::WorldRootCamera();
    if (!camera) return;
    float x{}, y{}, z{};
    if (
      !camera->WorldPtToScreenPt3(anchor, x, y, z, 1e-5f) || !Domain::Checks::Finite(x, y, z) || z <= 0 || x < 0 || x > 1 || y < 0 || y > 1)
      return;
    if (occlusion && !Raycast::Clear(camera->world.translate, anchor)) return;
    label.x = x;
    label.y = 1.0f - y;
    frame.labels.push_back(std::move(label));
  }

  export void Publish(Frame frame)
  {
    auto&            exchange = GetExchange();
    std::scoped_lock lock{exchange.mutex};
    exchange.frame = std::move(frame);
  }

  constexpr std::uint32_t BubbleFill        = 0x0A0A0C;
  constexpr std::uint32_t BubbleBorder      = 0x9C9A90;
  constexpr double        BubbleBorderAlpha = 55;    // Percent.
  constexpr double        BubblePadding     = 8;
  constexpr double        BubbleGap         = 6;     // Between the name line and the bubble bottom.
  constexpr double        HeaderGap         = 5;     // Between the header line and the text, the rule in the middle.
  constexpr float         HeaderScale       = 0.8f;  // Header font size relative to the bubble text.
  constexpr double        TextGutter        = 2;     // Flash text fields keep a 2px inner margin.
  constexpr double        LineHeightFactor  = 1.25;  // Estimated line advance relative to the font size.
  constexpr int           BubbleMaxLines    = 6;
  constexpr std::size_t   BubbleMaxChars    = 320;   // Code points before trimming starts.

  constexpr double RingRadius      = 0.4;            // Loading ring radius relative to the name font size.
  constexpr double RingStroke      = 0.15;           // Its stroke relative to the font size.
  constexpr double RingMinStroke   = 2;              // Pixels: thinner strokes vanish behind the outline.
  constexpr double RingGap         = 5;              // Between the name and the ring.
  constexpr double RingTrackAlpha  = 30;             // Percent: the empty part of the ring.
  constexpr double SpinArc         = 100;            // Degrees of the arc a measureless stage spins.
  constexpr double SpinTurnSeconds = 0.9;            // One turn of that arc.
  constexpr double EaseRate        = 6;              // Per second: how fast the drawn share follows the reported one.
  constexpr double RedrawShare     = 0.005;          // A smaller change of the drawn share is not redrawn.
  constexpr double ArcStepDegrees  = 10;             // One straight segment of a drawn arc.

  // Height reserved for the name line, from the font size only: the same value
  // whether the name is drawn or hidden.
  double NameBlock(float nameSize)
  {
    return static_cast<double>(nameSize) + 8;
  }

  struct NameField
  {
    RE::GFxValue  value;
    std::string   text;
    float         size{};
    std::uint32_t color{};
    double        width{}, height{};
  };

  // Object names in the HUD layer; a kind prefix keeps players and marks apart.
  std::string ObjectName(std::string_view prefix, const LabelKey& key)
  {
    std::string_view kind = "death";
    if (key.kind == LabelKind::Player)
    {
      kind = "player";
    }
    else if (key.kind == LabelKind::Note)
    {
      kind = "note";
    }
    return std::format("{}_{}_{}", prefix, kind, key.id);
  }

  struct BubbleClip
  {
    RE::GFxValue clip;
    RE::GFxValue head;
    RE::GFxValue text;
    std::string  header;
    std::string  content;
    BubbleStyle  style;
    double       width{}, height{};
    double       alpha{-1};
  };

  struct LoadingRing
  {
    using Clock = std::chrono::steady_clock;

    RE::GFxValue      clip;
    float             size{};
    std::uint32_t     color{};
    bool              spinning{};  // The measureless arc is on screen.
    double            shown{};     // The share on screen, easing towards the reported one.
    Clock::time_point updated{};
  };

  struct Entry
  {
    NameField   name;
    BubbleClip  bubble;
    LoadingRing ring;
  };

  struct Renderer
  {
    // Declare movie first: the managed GFx values must die before the movie.
    RE::GPtr<RE::GFxMovieView>                        movie;
    RE::GFxValue                                      layer;
    std::unordered_map<LabelKey, Entry, LabelKeyHash> entries;

    bool Bind(RE::GFxMovieView* current)
    {
      if (movie.get() != current)
      {
        entries.clear();
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

    bool MakeText(
      RE::GFxValue&      parent,
      RE::GFxValue&      out,
      const std::string& name,
      float              size,
      double             width,
      bool               wrap,
      std::uint32_t      color)
    {
      RE::GFxValue depth;
      if (!parent.Invoke("getNextHighestDepth", &depth) || !depth.IsNumber()) return false;
      RE::GFxValue args[]{
          RE::GFxValue(name.c_str()),
          depth,
          RE::GFxValue(0.0),
          RE::GFxValue(0.0),
          RE::GFxValue(width),
          RE::GFxValue(static_cast<double>(size + 8))
      };
      if (!parent.Invoke("createTextField", nullptr, args, 6) || !parent.GetMember(name.c_str(), &out) || !out.IsDisplayObject())
        return false;
      out.SetMember("selectable", RE::GFxValue(false));
      out.SetMember("multiline", RE::GFxValue(wrap));
      out.SetMember("wordWrap", RE::GFxValue(wrap));
      // Every text is centred: a name on its anchor, a bubble's lines inside the box.
      out.SetMember("autoSize", RE::GFxValue(wrap ? "left" : "center"));
      out.SetMember("embedFonts", RE::GFxValue(true));
      RE::GFxValue format;
      movie->CreateObject(&format, "TextFormat");
      format.SetMember("font", RE::GFxValue("$EverywhereFont"));
      format.SetMember("size", RE::GFxValue(static_cast<double>(size)));
      format.SetMember("color", RE::GFxValue(static_cast<double>(color & 0xFFFFFF)));
      format.SetMember("align", RE::GFxValue("center"));
      out.Invoke("setNewTextFormat", nullptr, &format, 1);
      return true;
    }

    static double Number(RE::GFxValue& object, const char* member)
    {
      RE::GFxValue value;
      object.GetMember(member, &value);
      return value.IsNumber() ? value.GetNumber() : 0;
    }

    static void Remove(NameField& field)
    {
      if (field.value.IsDisplayObject()) field.value.Invoke("removeTextField", nullptr);
      field = {};
    }

    static void Remove(BubbleClip& bubble)
    {
      if (bubble.clip.IsDisplayObject()) bubble.clip.Invoke("removeMovieClip", nullptr);
      bubble = {};
    }

    static void Remove(LoadingRing& ring)
    {
      if (ring.clip.IsDisplayObject()) ring.clip.Invoke("removeMovieClip", nullptr);
      ring = {};
    }

    static void Remove(Entry& entry)
    {
      Remove(entry.name);
      Remove(entry.bubble);
      Remove(entry.ring);
    }

    bool CreateName(NameField& field, const LabelKey& key, float size, std::uint32_t color)
    {
      if (!MakeText(layer, field.value, ObjectName("name", key), size, 400, false, color)) return false;
      Outline(field.value);
      field.size  = size;
      field.color = color;
      return true;
    }

    // Plain text with a stroke instead of a box: a tight, strong glow reads as
    // an outline and stays legible on snow and sky. Text filters need the
    // embedded font set in MakeText.
    void Outline(RE::GFxValue& text)
    {
      const RE::GFxValue glow[]{
          RE::GFxValue(0.0),    // color: black
          RE::GFxValue(1.0),    // alpha
          RE::GFxValue(3.0),    // blurX
          RE::GFxValue(3.0),    // blurY
          RE::GFxValue(12.0),   // strength
          RE::GFxValue(2.0),    // quality
          RE::GFxValue(false),  // inner
          RE::GFxValue(false)   // knockout
      };
      RE::GFxValue filter;
      movie->CreateObject(&filter, "flash.filters.GlowFilter", glow, 8);
      if (!filter.IsObject())
      {
        static bool warned = false;
        if (!warned) logger::warn("flash.filters.GlowFilter unavailable; firefly names are drawn without an outline");
        warned = true;
        return;
      }
      RE::GFxValue filters;
      movie->CreateArray(&filters);
      filters.PushBack(filter);
      text.SetMember("filters", filters);
    }

    // A nested clip owns the background drawing and the wrapped text field, so
    // the fade applies to the whole bubble through the clip's alpha while the
    // background opacity setting only affects the fill.
    bool CreateBubble(BubbleClip& bubble, const LabelKey& key, const BubbleStyle& style)
    {
      if (!layer.CreateEmptyMovieClip(&bubble.clip, ObjectName("bubble", key).c_str()) || !bubble.clip.IsDisplayObject()) return false;
      bubble.clip.SetMember("tabEnabled", RE::GFxValue(false));
      const double textWidth  = FieldWidth(style);
      const float  headerSize = std::max(8.0f, std::round(style.fontSize * HeaderScale));
      if (
        !MakeText(bubble.clip, bubble.head, "head", headerSize, textWidth, true, style.headerColor) ||
        !MakeText(bubble.clip, bubble.text, "text", style.fontSize, textWidth, true, style.textColor))
      {
        Remove(bubble);
        return false;
      }
      bubble.style = style;
      return true;
    }

    // Width both fields wrap at: the bubble's maximum minus padding.
    static double FieldWidth(const BubbleStyle& style)
    {
      return std::max(32.0, static_cast<double>(style.maxWidth) - 2 * BubblePadding + 2 * TextGutter);
    }

private:

    void DrawBubbleBackground(BubbleClip& bubble, bool both, double textY)
    {
      bubble.clip.Invoke("clear", nullptr);
      // Without a border the line is fully transparent; the outline path still
      // closes the fill.
      const RE::GFxValue line[]{
          RE::GFxValue(1.0),
          RE::GFxValue(static_cast<double>(BubbleBorder)),
          RE::GFxValue(bubble.style.border ? BubbleBorderAlpha : 0.0)
      };
      bubble.clip.Invoke("lineStyle", nullptr, line, 3);
      const RE::GFxValue fill[]{
          RE::GFxValue(static_cast<double>(BubbleFill)),
          RE::GFxValue(std::clamp(bubble.style.background, 0.0f, 1.0f) * 100.0)
      };
      bubble.clip.Invoke("beginFill", nullptr, fill, 2);
      const auto corner = [&](double x, double y, const char* method) {
        const RE::GFxValue point[]{RE::GFxValue(x), RE::GFxValue(y)};
        bubble.clip.Invoke(method, nullptr, point, 2);
      };
      corner(0.5, 0.5, "moveTo");
      corner(bubble.width - 0.5, 0.5, "lineTo");
      corner(bubble.width - 0.5, bubble.height - 0.5, "lineTo");
      corner(0.5, bubble.height - 0.5, "lineTo");
      corner(0.5, 0.5, "lineTo");
      bubble.clip.Invoke("endFill", nullptr);
      // A thin rule between the header and the text, in the border's look.
      if (both && bubble.style.border)
      {
        const double rule = textY - HeaderGap / 2;
        corner(BubblePadding, rule, "moveTo");
        corner(bubble.width - BubblePadding, rule, "lineTo");
      }
    }

public:

    // Sets the header and the text, trims the text to the line budget and
    // redraws the box around both. Runs only when a text or the style changed.
    void LayoutBubble(BubbleClip& bubble, const std::string& header, const std::string& content)
    {
      const auto maxHeight = BubbleMaxLines * bubble.style.fontSize * LineHeightFactor + 2 * TextGutter;
      // Fields were shrunk to the previous text; wrap the new one at full width.
      const double wrapWidth = FieldWidth(bubble.style);
      bubble.head.SetMember("_width", RE::GFxValue(wrapWidth));
      bubble.text.SetMember("_width", RE::GFxValue(wrapWidth));

      using Dreamsleeve::Utils::Text::CodePoints;
      using Dreamsleeve::Utils::Text::Ellipsize;
      // Plain UTF-8 text, never HTML or ActionScript from the network.
      bubble.head.SetText(header.c_str());
      const double headHeight = header.empty() ? 0 : Number(bubble.head, "textHeight");
      const double headWidth  = header.empty() ? 0 : Number(bubble.head, "textWidth");
      std::string  text       = CodePoints(content) > BubbleMaxChars ? Ellipsize(content, BubbleMaxChars) : content;
      bubble.text.SetText(text.c_str());
      double textHeight = content.empty() ? 0 : Number(bubble.text, "textHeight");
      for (int step = 0; textHeight > maxHeight && step < 24; ++step)
      {
        const auto length = CodePoints(text);
        if (length <= 8) break;
        text = Ellipsize(text, length - std::max<std::size_t>(4, length / 8));
        bubble.text.SetText(text.c_str());
        textHeight = Number(bubble.text, "textHeight");
      }

      const double textWidth = content.empty() ? 0 : Number(bubble.text, "textWidth");
      bubble.width =
        std::min(static_cast<double>(bubble.style.maxWidth), std::max(textWidth, headWidth) + 2 * BubblePadding + 2 * TextGutter);

      // Header on top, the text under it; each field keeps its 2px gutters.
      const bool   both  = !header.empty() && !content.empty();
      const double top   = BubblePadding - TextGutter;
      const double textY = header.empty() ? top : top + headHeight + 2 * TextGutter + (both ? HeaderGap : 0);
      bubble.height = (content.empty() ? textY - (both ? HeaderGap : 0) : textY + textHeight + 2 * TextGutter) + BubblePadding + TextGutter;

      // Shrink the fields to the measured lines so centred text sits in the box,
      // not in the wide field it wrapped in; the extra pixel keeps the wrap.
      const double              shown = bubble.width - 2 * BubblePadding + 2 * TextGutter + 1;
      RE::GFxValue::DisplayInfo head;
      head.SetPosition(top, top);
      bubble.head.SetDisplayInfo(head);
      bubble.head.SetMember("_width", RE::GFxValue(shown));
      RE::GFxValue::DisplayInfo body;
      body.SetPosition(top, textY);
      bubble.text.SetDisplayInfo(body);
      bubble.text.SetMember("_width", RE::GFxValue(shown));
      bubble.header  = header;
      bubble.content = content;

      DrawBubbleBackground(bubble, both, textY);
    }

    // Releases every GFx object and the movie reference while Scaleform is alive.
    void Reset()
    {
      for (auto& [id, entry] : entries)
        Remove(entry);
      entries.clear();
      if (layer.IsDisplayObject()) layer.Invoke("removeMovieClip", nullptr);
      layer.SetUndefined();
      movie.reset();
    }

    void DrawName(Entry& entry, const Label& label, double x, double y)
    {
      auto& field = entry.name;
      if (label.name.empty())
      {
        Remove(field);
        return;
      }
      if (field.value.IsDisplayObject() && (field.size != label.nameSize || field.color != label.nameColor)) Remove(field);
      if (!field.value.IsDisplayObject() && !CreateName(field, label.key, label.nameSize, label.nameColor)) return;
      if (field.text != label.name)
      {
        // Plain UTF-8 text, never HTML or ActionScript from the network.
        field.value.SetText(label.name.c_str());
        field.text   = label.name;
        field.width  = Number(field.value, "_width");
        field.height = Number(field.value, "_height");
      }
      RE::GFxValue::DisplayInfo info;
      info.SetPosition(x - field.width / 2, y - field.height);
      info.SetVisible(true);
      field.value.SetDisplayInfo(info);
    }

    // The bubble bottom sits a fixed gap above the name block; more lines grow it upwards.
    void DrawBubble(Entry& entry, const Label& label, double x, double y)
    {
      auto&       bubble = entry.bubble;
      const auto& style  = label.style;
      if (label.bubble.empty() && label.header.empty())
      {
        Remove(bubble);
        return;
      }
      if (bubble.clip.IsDisplayObject() && bubble.style != style) Remove(bubble);
      if (!bubble.clip.IsDisplayObject() && !CreateBubble(bubble, label.key, style)) return;
      if (bubble.content != label.bubble || bubble.header != label.header || bubble.width == 0)
        LayoutBubble(bubble, label.header, label.bubble);
      const double              alpha = std::clamp(static_cast<double>(label.bubbleAlpha), 0.0, 1.0) * 100.0;
      RE::GFxValue::DisplayInfo info;
      info.SetPosition(x - bubble.width / 2, y - NameBlock(label.nameSize) - BubbleGap - bubble.height);
      info.SetVisible(true);
      if (alpha != bubble.alpha) info.SetAlpha(alpha);
      bubble.alpha = alpha;
      bubble.clip.SetDisplayInfo(info);
    }

    // Straight segments along a circle around the clip's origin; angles in
    // degrees, clockwise on screen from the right.
    static void Arc(RE::GFxValue& clip, double radius, double from, double sweep)
    {
      const int steps = std::max(1, static_cast<int>(std::ceil(std::abs(sweep) / ArcStepDegrees)));
      for (int step = 0; step <= steps; ++step)
      {
        const double       angle = (from + sweep * step / steps) * std::numbers::pi / 180;
        const RE::GFxValue point[]{RE::GFxValue(radius * std::cos(angle)), RE::GFxValue(radius * std::sin(angle))};
        clip.Invoke(step == 0 ? "moveTo" : "lineTo", nullptr, point, 2);
      }
    }

    // A faint full track and, from the top, the arc of the share: the whole
    // ring is the name's color, the track a fraction of its opacity.
    static void DrawRing(LoadingRing& ring, double sweep)
    {
      const double radius = ring.size * RingRadius;
      const double stroke = std::max(RingMinStroke, ring.size * RingStroke);
      const auto   line   = [&](double alpha) {
        const RE::GFxValue style[]{
            RE::GFxValue(stroke),
            RE::GFxValue(static_cast<double>(ring.color & 0xFFFFFF)),
            RE::GFxValue(alpha),
            RE::GFxValue(false),
            RE::GFxValue("normal"),
            RE::GFxValue("round")
        };
        ring.clip.Invoke("lineStyle", nullptr, style, 6);
      };
      ring.clip.Invoke("clear", nullptr);
      line(RingTrackAlpha);
      Arc(ring.clip, radius, 0, 360);
      if (sweep <= 0) return;
      line(100);
      Arc(ring.clip, radius, -90, sweep);
    }

    // Beside the end of the name, on its middle line; without a name, where
    // the name would stand. A measureless stage turns its arc with the clock.
    void DrawLoading(Entry& entry, const Label& label, double x, double y)
    {
      auto& ring = entry.ring;
      if (!label.loading)
      {
        Remove(ring);
        return;
      }
      if (ring.clip.IsDisplayObject() && (ring.size != label.nameSize || ring.color != label.nameColor)) Remove(ring);
      if (!ring.clip.IsDisplayObject())
      {
        if (!layer.CreateEmptyMovieClip(&ring.clip, ObjectName("loading", label.key).c_str()) || !ring.clip.IsDisplayObject()) return;
        ring.clip.SetMember("tabEnabled", RE::GFxValue(false));
        Outline(ring.clip);
        ring.size  = label.nameSize;
        ring.color = label.nameColor;
      }

      // A ring created this frame has no elapsed time and is drawn at once.
      const auto   now = LoadingRing::Clock::now();
      const double elapsed =
        ring.updated == LoadingRing::Clock::time_point{} ? 0 : std::chrono::duration<double>(now - ring.updated).count();
      ring.updated    = now;
      double rotation = 0;
      if (const auto share = label.loading->share)
      {
        // A fresh ring or one that spun starts at the share; later ones ease.
        const double target = std::clamp(static_cast<double>(*share), 0.0, 1.0);
        const bool   fresh  = ring.spinning || elapsed == 0;
        const double eased  = fresh ? target : ring.shown + (target - ring.shown) * std::min(1.0, elapsed * EaseRate);
        const double next   = std::abs(target - eased) < RedrawShare ? target : eased;
        if (fresh || std::abs(next - ring.shown) >= RedrawShare || (next == target && ring.shown != target))
        {
          DrawRing(ring, next * 360);
          ring.shown    = next;
          ring.spinning = false;
        }
      }
      else
      {
        if (!ring.spinning)
        {
          DrawRing(ring, SpinArc);
          ring.spinning = true;
        }
        const double seconds = std::chrono::duration<double>(now.time_since_epoch()).count();
        rotation             = std::fmod(seconds / SpinTurnSeconds, 1.0) * 360;
      }

      const double              radius = label.nameSize * RingRadius;
      const bool                named  = entry.name.value.IsDisplayObject();
      const double              left   = named ? x + entry.name.width / 2 + RingGap + radius : x;
      const double              middle = named ? y - entry.name.height / 2 : y - NameBlock(label.nameSize) / 2;
      RE::GFxValue::DisplayInfo info;
      info.SetPosition(left, middle);
      info.SetRotation(rotation);
      info.SetVisible(true);
      ring.clip.SetDisplayInfo(info);
    }

    void Draw(RE::GFxMovieView* current, const Frame& frame)
    {
      if (!Bind(current)) return;
      std::unordered_set<LabelKey, LabelKeyHash> visible;
      visible.reserve(frame.labels.size());
      for (const auto& label : frame.labels)
        visible.insert(label.key);
      std::erase_if(entries, [&](auto& entry) {
        if (visible.contains(entry.first)) return false;
        Remove(entry.second);
        return true;
      });

      const auto rect = movie->GetVisibleFrameRect();
      if (!(rect.right > rect.left && rect.bottom > rect.top)) return;

      for (const auto& label : frame.labels)
      {
        auto&        entry = entries[label.key];
        const double x     = rect.left + label.x * (rect.right - rect.left);
        const double y     = rect.top + label.y * (rect.bottom - rect.top);
        DrawName(entry, label, x, y);
        DrawBubble(entry, label, x, y);
        DrawLoading(entry, label, x, y);
        if (!entry.name.value.IsDisplayObject() && !entry.bubble.clip.IsDisplayObject() && !entry.ring.clip.IsDisplayObject())
          entries.erase(label.key);
      }
    }
  };

  // The renderer is never destroyed: the DLL's static destructors run after the
  // engine has torn Scaleform down, so GFx values and the movie reference are
  // released explicitly (Release/Shutdown) and the object itself is leaked.
  struct Host
  {
    std::mutex mutex;
    Renderer*  renderer{};
    bool       stopped{};
  };

  Host& GetHost()
  {
    static Host host;
    return host;
  }

  // Called by the HUDMenu::AdvanceMovie hook in Hooks.ixx once the HUD advanced.
  export void Advance(RE::HUDMenu* menu)
  {
    Frame frame;
    {
      auto&            exchange = GetExchange();
      std::scoped_lock lock{exchange.mutex};
      frame = exchange.frame;
    }
    auto&            host = GetHost();
    std::scoped_lock lock{host.mutex};
    if (host.stopped) return;
    if (!host.renderer) host.renderer = new Renderer;
    host.renderer->Draw(menu->uiMovie.get(), frame);
  }

  // Main thread. Drops the layer, the text fields and the movie reference so a
  // replaced HUD (main menu, load) is not kept alive by this plugin.
  export void Release()
  {
    auto&            host = GetHost();
    std::scoped_lock lock{host.mutex};
    if (host.renderer) host.renderer->Reset();
  }

  // Main thread, on the frame quitGame is first seen: the UI still exists.
  export void Shutdown()
  {
    auto&            host = GetHost();
    std::scoped_lock lock{host.mutex};
    host.stopped = true;
    if (host.renderer) host.renderer->Reset();
  }

}
