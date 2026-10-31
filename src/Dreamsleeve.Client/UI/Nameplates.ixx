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

  // What became of a player's phantom, as a small mark beside the name.
  export enum class PhantomMark
  {
    // No phantom is coming: not offered, or out of reach. A faint ring.
    NotComing,
    // Its transfer has not started. A dashed ring turning slowly.
    Queued,
    // It does not fit the memory budget yet. A still amber dashed ring.
    WaitingMemory,
    // The share received fills the ring.
    Receiving,
    // Received bytes being checked and decoded. A spinning arc.
    Checking,
    // Its native scene being built. A spinning arc around a dot.
    Building,
    // The scene waits for the first pose of the player. A full ring around a dot.
    WaitingPose,
    // An attempt failed and another is due. A spinning amber arc.
    Retrying,
    // The server refused the model. A red ring with a bar.
    Refused,
    // The transfer failed for good. A red ring with a cross.
    TransferFailed,
    // The received bytes are not a valid model. A red ring with an exclamation mark.
    InvalidModel,
    // The model could not be turned into a scene. A red triangle.
    SceneFailed
  };

  export struct PhantomStatus
  {
    PhantomMark mark{};
    float       share{};  // Receiving only: the share of the model received.

    bool operator==(const PhantomStatus&) const = default;
  };

  // All texts are plain UTF-8. An empty name hides the name field but keeps
  // its baseline, so the bubble never jumps when names are switched off. A
  // header is a smaller line on top inside the same box (a mark's date).
  export struct Label
  {
    LabelKey                     key{};
    std::string                  name;
    float                        nameSize{};
    std::uint32_t                nameColor{Dreamsleeve::Host::Hud::DefaultTextColor};
    std::string                  header;
    std::string                  bubble;
    float                        bubbleAlpha{1.0f};
    BubbleStyle                  style;
    std::optional<PhantomStatus> status;
    float                        x{}, y{};  // Filled by Add: normalized screen position of the anchor.
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
    if (REL::Module::IsVR() || (label.name.empty() && label.bubble.empty() && label.header.empty() && !label.status)) return;
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
  constexpr double        BubbleBorderAlpha = 55;     // Percent.
  constexpr double        BubblePadding     = 8;
  constexpr double        BubbleGap         = 6;      // Between the name line and the bubble bottom.
  constexpr double        HeaderGap         = 5;      // Between the header line and the text, the rule in the middle.
  constexpr float         HeaderScale       = 0.8f;   // Header font size relative to the bubble text.
  constexpr double        TextGutter        = 2;      // Flash text fields keep a 2px inner margin.
  constexpr double        LineHeightFactor  = 1.25;   // Estimated line advance relative to the font size.
  constexpr int           BubbleMaxLines    = 6;
  constexpr std::size_t   BubbleMaxChars    = 320;    // Code points before trimming starts.

  constexpr double RingRadius       = 0.4;            // Phantom mark radius relative to the name font size.
  constexpr double RingStroke       = 0.15;           // Its stroke relative to the font size.
  constexpr double RingMinStroke    = 2;              // Pixels: thinner strokes vanish behind the outline.
  constexpr double RingGap          = 5;              // Between the name and the ring.
  constexpr double RingTrackAlpha   = 30;             // Percent: the empty part of the ring.
  constexpr double SpinArc          = 100;            // Degrees of the arc a measureless stage spins.
  constexpr double SpinTurnSeconds  = 0.9;            // One turn of that arc.
  constexpr double QueueTurnSeconds = 4;              // One turn of the queued mark's dashes.
  constexpr int    RingDashes       = 8;              // Dashes of a dashed ring.
  constexpr double RingDashShare    = 0.55;           // The part of each dash's slot it fills.
  constexpr double GlyphStroke      = 0.75;           // Inner glyph stroke relative to the ring's.

  constexpr std::uint32_t MarkAmber      = 0xF2B13C;  // Waiting on something that may yet come.
  constexpr std::uint32_t MarkRed        = 0xE5484D;  // The phantom will not come for this offer.
  constexpr double        EaseRate       = 6;         // Per second: how fast the drawn share follows the reported one.
  constexpr double        RedrawShare    = 0.005;     // A smaller change of the drawn share is not redrawn.
  constexpr double        ArcStepDegrees = 10;        // One straight segment of a drawn arc.

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

  // How a mark is drawn: a figure around the clip's origin and a glyph inside.
  struct MarkLook
  {
    enum class Figure
    {
      Track,     // A faint full ring.
      Dashes,    // A dashed ring.
      Spinner,   // A faint ring with a bright arc.
      Share,     // A faint ring filled by the reported share.
      Full,      // A bright full ring.
      Triangle,  // A warning triangle instead of a ring.
    };

    enum class Glyph
    {
      None,
      Dot,
      Bar,
      Cross,
      Exclamation,
    };

    Figure                       figure{};
    Glyph                        glyph{};
    std::optional<std::uint32_t> tint;           // Absent: the name's color.
    double                       turnSeconds{};  // Zero: the mark stands still.
  };

  constexpr MarkLook LookOf(PhantomMark mark)
  {
    using Figure = MarkLook::Figure;
    using Glyph  = MarkLook::Glyph;
    switch (mark)
    {
      case PhantomMark::NotComing:
        return {Figure::Track, Glyph::None};
      case PhantomMark::Queued:
        return {Figure::Dashes, Glyph::None, std::nullopt, QueueTurnSeconds};
      case PhantomMark::WaitingMemory:
        return {Figure::Dashes, Glyph::None, MarkAmber};
      case PhantomMark::Receiving:
        return {Figure::Share, Glyph::None};
      case PhantomMark::Checking:
        return {Figure::Spinner, Glyph::None, std::nullopt, SpinTurnSeconds};
      case PhantomMark::Building:
        return {Figure::Spinner, Glyph::Dot, std::nullopt, SpinTurnSeconds};
      case PhantomMark::WaitingPose:
        return {Figure::Full, Glyph::Dot};
      case PhantomMark::Retrying:
        return {Figure::Spinner, Glyph::None, MarkAmber, SpinTurnSeconds};
      case PhantomMark::Refused:
        return {Figure::Full, Glyph::Bar, MarkRed};
      case PhantomMark::TransferFailed:
        return {Figure::Full, Glyph::Cross, MarkRed};
      case PhantomMark::InvalidModel:
        return {Figure::Full, Glyph::Exclamation, MarkRed};
      case PhantomMark::SceneFailed:
        return {Figure::Triangle, Glyph::None, MarkRed};
    }
    return {};
  }

  struct MarkClip
  {
    using Clock = std::chrono::steady_clock;

    RE::GFxValue               clip;
    float                      size{};
    std::uint32_t              color{};
    std::optional<PhantomMark> drawn;    // The mark on screen.
    double                     shown{};  // The share on screen, easing towards the reported one.
    Clock::time_point          updated{};
  };

  struct Entry
  {
    NameField  name;
    BubbleClip bubble;
    MarkClip   mark;
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

    static void Remove(MarkClip& mark)
    {
      if (mark.clip.IsDisplayObject()) mark.clip.Invoke("removeMovieClip", nullptr);
      mark = {};
    }

    static void Remove(Entry& entry)
    {
      Remove(entry.name);
      Remove(entry.bubble);
      Remove(entry.mark);
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

    static void Segment(RE::GFxValue& clip, double fromX, double fromY, double toX, double toY)
    {
      const RE::GFxValue from[]{RE::GFxValue(fromX), RE::GFxValue(fromY)};
      const RE::GFxValue to[]{RE::GFxValue(toX), RE::GFxValue(toY)};
      clip.Invoke("moveTo", nullptr, from, 2);
      clip.Invoke("lineTo", nullptr, to, 2);
    }

    static void DrawGlyph(RE::GFxValue& clip, MarkLook::Glyph glyph, double radius)
    {
      // A segment too short to see draws as a dot through its round caps.
      constexpr double Point = 0.01;
      switch (glyph)
      {
        case MarkLook::Glyph::None:
          break;
        case MarkLook::Glyph::Dot:
          Segment(clip, 0, 0, Point, 0);
          break;
        case MarkLook::Glyph::Bar:
          Segment(clip, -radius * 0.5, 0, radius * 0.5, 0);
          break;
        case MarkLook::Glyph::Cross:
          Segment(clip, -radius * 0.4, -radius * 0.4, radius * 0.4, radius * 0.4);
          Segment(clip, -radius * 0.4, radius * 0.4, radius * 0.4, -radius * 0.4);
          break;
        case MarkLook::Glyph::Exclamation:
          Segment(clip, 0, -radius * 0.5, 0, radius * 0.1);
          Segment(clip, 0, radius * 0.5, Point, radius * 0.5);
          break;
      }
    }

    // The whole mark in one color, the faint parts a fraction of its opacity;
    // arcs start from the top. The sweep is that of the share or the spinner.
    static void DrawMark(MarkClip& mark, const MarkLook& look, double sweep)
    {
      const double radius = mark.size * RingRadius;
      const double stroke = std::max(RingMinStroke, mark.size * RingStroke);
      const auto   line   = [&](double width, double alpha) {
        const RE::GFxValue style[]{
            RE::GFxValue(width),
            RE::GFxValue(static_cast<double>(mark.color & 0xFFFFFF)),
            RE::GFxValue(alpha),
            RE::GFxValue(false),
            RE::GFxValue("normal"),
            RE::GFxValue("round")
        };
        mark.clip.Invoke("lineStyle", nullptr, style, 6);
      };
      mark.clip.Invoke("clear", nullptr);
      switch (look.figure)
      {
        case MarkLook::Figure::Track:
          line(stroke, RingTrackAlpha);
          Arc(mark.clip, radius, 0, 360);
          break;
        case MarkLook::Figure::Dashes:
          line(stroke, 100);
          for (int dash = 0; dash < RingDashes; ++dash)
            Arc(mark.clip, radius, dash * 360.0 / RingDashes, RingDashShare * 360.0 / RingDashes);
          break;
        case MarkLook::Figure::Spinner:
        case MarkLook::Figure::Share:
          line(stroke, RingTrackAlpha);
          Arc(mark.clip, radius, 0, 360);
          line(stroke, 100);
          if (sweep > 0) Arc(mark.clip, radius, -90, sweep);
          break;
        case MarkLook::Figure::Full:
          line(stroke, 100);
          Arc(mark.clip, radius, 0, 360);
          break;
        case MarkLook::Figure::Triangle:
          line(stroke, 100);
          Segment(mark.clip, 0, -radius * 1.1, radius, radius * 0.75);
          Segment(mark.clip, radius, radius * 0.75, -radius, radius * 0.75);
          Segment(mark.clip, -radius, radius * 0.75, 0, -radius * 1.1);
          break;
      }
      line(stroke * GlyphStroke, 100);
      DrawGlyph(mark.clip, look.glyph, radius);
    }

    // The share on screen follows the reported one: a fresh mark starts at it,
    // later reports ease towards it and tiny steps are not redrawn.
    static void DrawShare(MarkClip& mark, const MarkLook& look, float share, double elapsed)
    {
      const double target = std::clamp(static_cast<double>(share), 0.0, 1.0);
      const bool   fresh  = mark.drawn != PhantomMark::Receiving || elapsed == 0;
      const double eased  = fresh ? target : mark.shown + (target - mark.shown) * std::min(1.0, elapsed * EaseRate);
      const double next   = std::abs(target - eased) < RedrawShare ? target : eased;
      if (fresh || std::abs(next - mark.shown) >= RedrawShare || (next == target && mark.shown != target))
      {
        DrawMark(mark, look, next * 360);
        mark.shown = next;
      }
    }

    // Beside the end of the name, on its middle line; without a name, where
    // the name would stand. A moving mark turns with the clock.
    void DrawStatus(Entry& entry, const Label& label, double x, double y)
    {
      auto& mark = entry.mark;
      if (!label.status)
      {
        Remove(mark);
        return;
      }
      const auto look  = LookOf(label.status->mark);
      const auto color = look.tint.value_or(label.nameColor);
      if (mark.clip.IsDisplayObject() && (mark.size != label.nameSize || mark.color != color)) Remove(mark);
      if (!mark.clip.IsDisplayObject())
      {
        if (!layer.CreateEmptyMovieClip(&mark.clip, ObjectName("phantom-mark", label.key).c_str()) || !mark.clip.IsDisplayObject()) return;
        mark.clip.SetMember("tabEnabled", RE::GFxValue(false));
        Outline(mark.clip);
        mark.size  = label.nameSize;
        mark.color = color;
      }

      // A mark created this frame has no elapsed time and is drawn at once.
      const auto   now     = MarkClip::Clock::now();
      const double elapsed = mark.updated == MarkClip::Clock::time_point{} ? 0 : std::chrono::duration<double>(now - mark.updated).count();
      mark.updated         = now;
      if (look.figure == MarkLook::Figure::Share)
        DrawShare(mark, look, label.status->share, elapsed);
      else if (mark.drawn != label.status->mark)
        DrawMark(mark, look, look.figure == MarkLook::Figure::Spinner ? SpinArc : 0);
      mark.drawn = label.status->mark;

      const double              seconds = std::chrono::duration<double>(now.time_since_epoch()).count();
      const double              radius  = label.nameSize * RingRadius;
      const bool                named   = entry.name.value.IsDisplayObject();
      const double              left    = named ? x + entry.name.width / 2 + RingGap + radius : x;
      const double              middle  = named ? y - entry.name.height / 2 : y - NameBlock(label.nameSize) / 2;
      RE::GFxValue::DisplayInfo info;
      info.SetPosition(left, middle);
      info.SetRotation(look.turnSeconds > 0 ? std::fmod(seconds / look.turnSeconds, 1.0) * 360 : 0);
      info.SetVisible(true);
      mark.clip.SetDisplayInfo(info);
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
        DrawStatus(entry, label, x, y);
        if (!entry.name.value.IsDisplayObject() && !entry.bubble.clip.IsDisplayObject() && !entry.mark.clip.IsDisplayObject())
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
